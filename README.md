# HKShop

A Vietnamese marketplace built with .NET 10, Blazor, ASP.NET Core Identity, EF Core, and SQL Server. The demo supports VND purchases through SePay Sandbox and simulated COD for HKShop's own store. Sellers handle fulfillment and returns for their own orders. HKShop tracks refunds against the buyer's captured payment.

[Architecture and workflow diagrams](docs/Architecture.md) · [Staging and recovery](docs/Operations.md)

[Marketplace roadmap and database diagrams](docs/Marketplace-Plan.md)

## Run locally

Open PowerShell in the directory containing `HKShop.slnx`. Checkout uses SQL Server and Kafka. To start them with Mailpit in Docker, copy `deploy/.env.example` to `deploy/.env`, set `HKSHOP_SQL_SA_PASSWORD`, and run the Compose command.

```powershell
docker compose --env-file deploy/.env -f deploy/compose.yaml up -d --build sql mailpit seq notification
```

Development defaults to Kafka at `127.0.0.1:9092`. Compose initializes the checkout, ordering and Notification dead-letter topics. Notification creates and migrates its own local SQL database, `HKShopNotification`. Its job table, `ConfirmationEmails`, retains sent jobs for replay protection. To reuse an existing broker, initialize the same topics on that broker and configure both processes to reach it.

```powershell
foreach ($topic in 'hkshop.checkout.requests.v1','hkshop.ordering.events.v1','hkshop.ordering.events.v1.notification.dlq.v1') {
    docker exec <kafka-container> /opt/kafka/bin/kafka-topics.sh --bootstrap-server <broker-listener> --create --if-not-exists --topic $topic --partitions 1 --replication-factor 1
}
```

Configure the same database for the EF tool and the web app. Use the original database connection only when you intend to upgrade it. A database that contains the original `dbo.Products` table should also contain the existing `20261111082232_InitialProducts` migration record. Take a backup before upgrading existing data.

```powershell
$env:ConnectionStrings__HKShop = 'Server=localhost,1433;Database=HKShop;User ID=sa;Password=REPLACE_LOCALLY;Encrypt=True;TrustServerCertificate=True'
dotnet tool restore
dotnet ef database update --project src/HKShop.Infrastructure --startup-project src/HKShop.Web --context ShopDbContext
dotnet dev-certs https --trust

dotnet user-secrets set 'Bootstrap:AdminEmail' 'admin@example.test' --project src/HKShop.Web
dotnet user-secrets set 'Bootstrap:AdminPassword' 'REPLACE_WITH_A_DEV_PASSWORD' --project src/HKShop.Web
dotnet run --project src/HKShop.Web -- --bootstrap-admin
dotnet user-secrets remove 'Bootstrap:AdminPassword' --project src/HKShop.Web

dotnet run --project src/HKShop.Web
```

Open `https://localhost:7243`. The local test inbox is `http://localhost:8025` and structured logs (Seq) are at `http://localhost:5341`.
- **Commerce Platform (`/`, `/account/login`, `/seller`)**: Dedicated exclusively to Customers and Sellers (`CommerceLayout`).
- **EOffice Platform (`/eoffice/login`, `/eoffice`, `/eoffice/shipper`)**: Dedicated exclusively to HKShop Staff, Administrators, and Shippers (`EOfficeLayout`).

The first admin can sign in at `/eoffice/login`, open `/eoffice/security`, add the displayed TOTP key to an authenticator, and enter a code. Sign in again with MFA at `/eoffice/login` to open `/eoffice/products`. Create a category and product, upload an image, create an active SKU with a positive VND price, adjust 1st-party stock at `/eoffice/inventory`, and publish.

When upgrading, the migration keeps each original product ID, name, and price and creates a draft SKU with zero stock. A fractional legacy price stays in the original product row. Set a whole-VND price on its SKU before publishing.

## Seller onboarding and access

Apply the latest EF Core migration before opening the seller console.

1. Sign in with a confirmed customer email and open **Account → Register a store** (`/account/store`). Enter product names and whole-VND prices manually or upload a UTF-8 CSV up to 5 MB with the headers `product_name,price_vnd`. Enter the national ID and business license numbers and upload both sides of the ID card and a copy of the license.
2. An EOffice administrator reviews the application at `/eoffice/sellers` and records a decision. HKShop posts the result to the account and queues an email through the configured SMTP server. Approved applicants can open their seller workspace after signing in with MFA.
3. As an owner, invite staff by email and select their role. The invitee opens the emailed link and accepts it with the matching confirmed account and MFA. Configure `Shop:PublicOrigin` to your HTTPS origin outside Development so the outbox can send working invitation links.
4. Owners and finance staff can view and export sales for their active seller. Owners can also view that seller's audit history and revoke invitations or staff memberships. Platform administrators can suspend and reactivate third-party sellers.
5. Store verification numbers and documents are encrypted using HKShop's Data Protection keys. Back up the configured key ring with the database. Owners can permanently close their store to stop new sales and remove its verification numbers and files while keeping order and settlement history.

## Marketplace offers and inventory

The catalog ownership migration assigns existing products and imports to HKShop. Each seller can list products and variants owned by their store.

1. Open **Offers and inventory** (`/seller/{id}/catalog`) as the owner or catalog manager of an approved store. Upload the product name, category, description, and photos. Enter an offer code, price, and starting quantity for each variant.
2. EOffice reviews the product details and its variant offers at `/eoffice/marketplace`. Approval of the first variant offer publishes the product. Each approved offer becomes eligible for the storefront. The seller can adjust prices and inventory in their workspace.
3. Buyers can purchase offers from several stores in one checkout. HKShop creates a separate seller order for each store.

## Fulfillment and returns

Apply `MarketplaceFulfillmentReturnsAndRefunds` before starting the updated app. An owner or fulfillment staff member can open **Đơn hàng và hàng trả** from their seller workspace to pack, ship, deliver, or cancel that seller's part of a paid order. Platform staff can handle the same actions from the buyer order in the admin console.

The buyer order shows tracking by seller. Buyers can request returns for eligible items within 14 days of that seller's delivery. After inspection, sellable units return to the original location. The refund uses the original discounted line amounts. A seller cancellation before dispatch also refunds that seller's shipping charge.

## Marketplace settlement

Apply `MarketplaceSettlement` before starting the updated app. New captures and confirmed refunds post balanced VND journals using the checkout price and commission snapshots. Historical captures are outside automated settlement. Proceeds become available after both capture and delivery clear the `Shop:SettlementHoldDays` hold (14 days by default). Open returns and pending refunds keep the affected proceeds on hold.

Open the settlement statement from a seller workspace as the owner or a finance staff member. The statement shows pending, available, and payout-in-transit balances, including any deficit from a refund after payout. A request reserves funds immediately. An admin reviews requests at **Quản lý → Chi trả** and checks capture and refund reconciliation at **Quản lý → Thanh toán**.

Payouts use a simulator. In a local or staging environment, set `PayoutSimulation__RejectTransfers=true` to simulate a provider failure or `PayoutSimulation__LoseSubmissionResponse=true` to simulate a timeout after submission. The worker reconciles the saved transfer before settling or releasing the reserved amount.

## Enable SePay Sandbox

1. Activate **Cổng thanh toán → Sandbox** in your [SePay dashboard](https://my.sepay.vn). Copy the sandbox merchant ID and merchant secret. Configure a separate IPN secret with auth type `SECRET_KEY`.
2. Set `Shop:PublicOrigin` to your public HTTPS staging origin or HTTPS tunnel. Set `SePay:MerchantId`, `SePay:MerchantSecret`, and `SePay:IpnSecret` with `dotnet user-secrets` in Development or mounted/staging configuration. When using a tunnel, run the app so that HTTPS forwarding is trusted from that tunnel's proxy. Keep `AllowedHosts` consistent with the public host.
3. Set the SePay IPN URL to `https://YOUR_HOST/payments/sepay/ipn`. Checkout will display SePay once these settings are present. The three browser return URLs lead to the customer's order page.

[SePay Sandbox](https://developer.sepay.vn/vi/cong-thanh-toan/sandbox) · [SePay IPN](https://developer.sepay.vn/vi/cong-thanh-toan/IPN)

Set `SePay:SimulateRefunds=true` in local or staging configuration (environment variable `SePay__SimulateRefunds=true`) to have the background worker submit and reconcile simulated refunds. Each simulated refund keeps the same `SIM-` reference on retry. By default, the worker routes SePay refunds to **Admin → Payments** for external reconciliation. Use the refund's business key to track the external refund, then record the confirmed reference in HKShop. COD refunds continue to use an admin-recorded simulated reference.

The SePay Payment Gateway documentation covers payment lookup and cancellation. Its documented automated refund API uses VA bank collection orders and separate credentials. [SePay Payment Gateway API](https://developer.sepay.vn/vi/cong-thanh-toan/API/tong-quan) · [SePay VA refunds](https://developer.sepay.vn/vi/sepay-api/v2/hoan-tien/hoan-tien-don-hang)

## Verification you can run

```powershell
dotnet test tests/HKShop.UnitTests
$env:HKSHOP_TEST_SQL = $env:ConnectionStrings__HKShop
$env:HKSHOP_TEST_KAFKA = '127.0.0.1:9092'
dotnet test tests/HKShop.IntegrationTests

dotnet build tests/HKShop.BrowserTests
pwsh tests/HKShop.BrowserTests/bin/Debug/net10.0/playwright.ps1 install chromium
$env:HKSHOP_BROWSER_URL = 'https://localhost:7243'
dotnet test tests/HKShop.BrowserTests --no-build
```

SQL tests require a development login allowed to create and drop isolated `HKShopTests_*` databases. Browser tests expect an already running app. The test projects are opt-in where they depend on a running service.

For manual verification, register a customer and confirm the email from Mailpit. Add an address and place a SePay Sandbox order with two sellers from a separate browser session. Cancel one seller order before dispatch, then pack, ship, deliver, and process a partial return for the other. Check each seller's stock movements, the buyer's tracking and refund amounts, and Admin Payments. Exercise the same first-party fulfillment and return flow with simulated COD. Also try stale quotes, payment expiry, repeated submits, and unauthorized admin access.

The original `HKShhop/` scaffold is archived under `archive/HKShhop/`. The solution runs the host in `src/HKShop.Web`.

## Import the ABO catalog

Download `abo-listings.tar` and `abo-images-small.tar` into the same directory. Apply the
`AboCatalogData` migration to the HKShop database, set `ConnectionStrings__HKShop`,
and pass the archive directory and the storefront's configured media directory to the importer.
[Official ABO downloads](https://amazon-berkeley-objects.s3.amazonaws.com/index.html#download)

```powershell
dotnet run --project tools/HKShop.AboImport --configuration Release -- 'C:\path\to\abo' '.\src\HKShop.Web\data\media'
```

Imported products start as drafts. The importer preserves the original JSON in `AboListings`,
stores localized text in `ProductTranslations`, and uses the default language for the
existing storefront fields. Each catalog image is stored once on disk and linked to the
products that use it. Re-running the command resumes by source identity and preserves edits
to imported product records.

The data and photos are credited to Amazon.com and the dataset contributors listed by ABO.
HKShop maps categories, selects default text, and converts description markup to plain text.
[ABO contributors and attribution](https://amazon-berkeley-objects.s3.amazonaws.com/index.html#attribution) ·
[CC BY 4.0 license](https://creativecommons.org/licenses/by/4.0/)

