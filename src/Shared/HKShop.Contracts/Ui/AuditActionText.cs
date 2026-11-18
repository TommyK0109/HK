namespace HKShop.Application.Reporting;

public static class AuditActionText
{
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["Danh mục"] = "Category",
        ["Sản phẩm"] = "Product",
        ["Biến thể"] = "Variant",
        ["Ảnh sản phẩm"] = "Product image",
        ["Đăng bán"] = "Publish",
        ["Ẩn sản phẩm"] = "Unpublish product",
        ["Cập nhật kho"] = "Update stock",
        ["Cập nhật mã giảm giá"] = "Update discount code",
        ["Hủy đơn"] = "Cancel order",
        ["Đóng gói"] = "Pack order",
        ["Xuất hàng"] = "Dispatch",
        ["Xác nhận giao hàng"] = "Confirm delivery",
        ["Sự cố giao hàng"] = "Delivery issue",
        ["Nhận lại bưu kiện"] = "Receive returned parcel",
        ["Duyệt trả hàng"] = "Review return",
        ["Kiểm hàng trả"] = "Inspect returned items",
        ["Đối soát SePay"] = "Reconcile SePay",
        ["Hoàn tiền mô phỏng"] = "Simulated refund",
        ["Xác nhận đối soát hoàn tiền"] = "Confirm refund reconciliation",
        ["Gửi duyệt chào bán"] = "Submit offer for review",
        ["Gửi lại chào bán"] = "Resubmit offer",
        ["Cập nhật chào bán"] = "Update offer",
        ["Ngừng chào bán"] = "Archive offer",
        ["Cập nhật kho chào bán"] = "Update offer stock",
        ["Đề xuất sản phẩm"] = "Propose product",
        ["Duyệt chào bán"] = "Approve offer",
        ["Từ chối chào bán"] = "Reject offer",
        ["Duyệt đề xuất sản phẩm"] = "Approve product proposal",
        ["Từ chối đề xuất sản phẩm"] = "Reject product proposal",
        ["Đăng ký gian hàng"] = "Register store",
        ["Duyệt gian hàng"] = "Approve store",
        ["Khôi phục gian hàng"] = "Restore store",
        ["Tạm ngưng gian hàng"] = "Suspend store",
        ["Mời thành viên gian hàng"] = "Invite store member",
        ["Chấp nhận lời mời gian hàng"] = "Accept store invitation",
        ["Thu hồi quyền thành viên gian hàng"] = "Revoke store membership",
        ["Thu hồi lời mời gian hàng"] = "Revoke store invitation",
        ["Mời nhân viên"] = "Invite staff",
        ["Cập nhật người dùng"] = "Update user",
        ["Đăng nhập quản trị"] = "Admin sign-in",
    };

    public static string Display(string value) => Labels.TryGetValue(value, out var label) ? label : value;

    public static string[] MatchingKeys(string search) => Labels
        .Where(pair => pair.Value.Contains(search, StringComparison.OrdinalIgnoreCase))
        .Select(pair => pair.Key)
        .ToArray();
}
