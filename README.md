# HKShop

A Vietnamese marketplace built with .NET 10, Blazor, ASP.NET Core Identity, EF Core, and SQL Server. The demo supports VND purchases through SePay Sandbox and simulated COD for HKShop's own store. Sellers handle fulfillment and returns for their own orders. HKShop tracks refunds against the buyer's captured payment.

[Architecture and workflow diagrams](docs/Architecture.md) · [Staging and recovery](docs/Operations.md)

[Marketplace roadmap and database diagrams](docs/Marketplace-Plan.md)

## Run locally

Open PowerShell in the directory containing `HKShop.slnx`. Checkout uses SQL Server and Kafka. To start them with Mailpit in Docker, copy `deploy/.env.example` to `deploy/.env`, set `HKSHOP_SQL_SA_PASSWORD`, and run the Compose command.

```powershell
docker compose --env-file deploy/.env -f deploy/compose.yaml up -d sql mailpit seq kafka-init
```

Development defaults to Kafka at `127.0.0.1:9092`. To reuse an existing Kafka container, start only the other services and set `Kafka__BootstrapServers` if the advertised address differs. Create the checkout topic once using the broker's CLI.

```powershell
docker exec <kafka-container> /opt/kafka/bin/kafka-topics.sh --bootstrap-server <broker-listener> --create --if-not-exists --topic hkshop.checkout.requests.v1 --partitions 1 --replication-factor 1
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

Apply the latest EF Core migration before opening the seller console. The migration assigns existing orders to HKShop's first-party seller. Platform staff continue to pack, ship, and complete existing COD orders through the EOffice console (`/eoffice/orders`).

1. Sign in with a confirmed customer email at `/account/login` and register a seller from **Seller center** (`/seller`). A platform administrator signs in with MFA at `/eoffice/login` and approves the seller at `/eoffice/sellers`.
2. Enroll an authenticator under `/account/security`, then sign in with MFA. Choose a seller workspace from `/seller`. If your account belongs to several sellers, return to that list to switch workspaces.
3. As an owner, invite staff by email and select their role. The invitee opens the emailed link and accepts it with the matching confirmed account and MFA. Configure `Shop:PublicOrigin` to your HTTPS origin outside Development so the outbox can send working invitation links.
4. Owners and finance staff can view and export sales for their active seller. Owners can also view that seller's audit history and revoke invitations or staff memberships. Platform administrators can suspend and reactivate third-party sellers.

## Marketplace offers and inventory

Apply `MarketplaceOffersAndInventory` before starting the updated app. The migration creates HKShop offers from existing SKUs, carries over stock and movement history, and keeps 1st-party offers synchronized with the EOffice catalog and inventory screens.

1. Open **Offers and inventory** (`/seller/{id}/catalog`) from an approved seller workspace as an owner or catalog manager. Search for a published variant, enter a seller SKU code, price, and initial stock quantity, and submit the offer. Adjust seller stock (`/api/commerce/inventory/seller/{sellerId}/offers/{offerId}/stock`) and view the offer's movement history there.
2. Approve offers and review product proposals at `/eoffice/marketplace`. An approved product proposal creates a draft in the shared catalog. Add an image and an active variant in `/eoffice/products/{id}`, then publish it so sellers can submit offers. EOffice automatically aggregates total active offer stock (`TotalOfferOnHand` and `TotalOfferAvailable`) on the SKU alongside 1st-party stock. Use a price of `0` on the variant when HKShop will leave the selling to third-party sellers.
3. The storefront shows a default offer based on stock availability, price, then offer ID. Buyers can choose another seller on the product page and pay for offers from multiple sellers in one SePay checkout.

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

