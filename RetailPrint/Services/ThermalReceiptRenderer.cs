using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using RetailPrint.Models;

namespace RetailPrint.Services;

internal static class ThermalReceiptRenderer
{
    internal const float Dpi = 203f;

    public static Bitmap Render(RetailPrintPayload payload, int paperWidthMm)
    {
        if (paperWidthMm is not (58 or 80))
            throw new InvalidOperationException("Khổ giấy chỉ hỗ trợ 58 mm hoặc 80 mm.");

        var width = paperWidthMm == 80 ? 576 : 384;
        using var measureBitmap = new Bitmap(1, 1);
        measureBitmap.SetResolution(Dpi, Dpi);
        using var measureGraphics = Graphics.FromImage(measureBitmap);
        Configure(measureGraphics);

        var height = Math.Max(220, (int)Math.Ceiling(DrawReceipt(
            measureGraphics,
            payload,
            paperWidthMm,
            width,
            draw: false)));

        var bitmap = new Bitmap(width, height);
        bitmap.SetResolution(Dpi, Dpi);
        using var graphics = Graphics.FromImage(bitmap);
        Configure(graphics);
        graphics.Clear(Color.White);
        DrawReceipt(graphics, payload, paperWidthMm, width, draw: true);
        return bitmap;
    }

    private static void Configure(Graphics graphics)
    {
        graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    private static float DrawReceipt(
        Graphics graphics,
        RetailPrintPayload payload,
        int paperWidthMm,
        int width,
        bool draw)
    {
        var paperScale = paperWidthMm == 80 ? 1f : 0.86f;
        var configuredScale = Math.Clamp(payload.FontSizePercent, 80, 140) / 100f;
        var scale = paperScale * configuredScale;
        var margin = paperWidthMm == 80 ? 18f : 14f;
        var contentWidth = width - margin * 2f;
        var y = 16f;

        using var companyFont = Font(22f * scale, FontStyle.Bold);
        using var titleFont = Font(30f * scale, FontStyle.Bold);
        using var subtitleFont = Font(17f * scale, FontStyle.Bold);
        using var bodyFont = Font(18f * scale, FontStyle.Regular);
        using var bodyBoldFont = Font(18f * scale, FontStyle.Bold);
        using var productFont = Font(22f * scale, FontStyle.Bold);
        using var smallFont = Font(15f * scale, FontStyle.Regular);
        using var totalLabelFont = Font(25f * scale, FontStyle.Regular);
        using var totalValueFont = Font(31f * scale, FontStyle.Bold);
        using var linePen = new Pen(Color.Black, Math.Max(1f, 2f * scale));
        using var itemPen = new Pen(Color.Black, 1f) { DashStyle = DashStyle.Dash };

        y += DrawBlock(graphics, Clean(payload.Heading), companyFont, margin, y, contentWidth, StringAlignment.Center, draw);
        y += Space(4f, scale);
        y += DrawBlock(graphics, Clean(payload.Title), titleFont, margin, y, contentWidth, StringAlignment.Center, draw);

        var subtitle = Clean(payload.Subtitle);
        if (subtitle.Length > 0)
        {
            y += Space(4f, scale);
            y += DrawBlock(graphics, subtitle, subtitleFont, margin, y, contentWidth, StringAlignment.Center, draw);
        }

        var documentNumber = Clean(payload.DocumentNumber);
        if (documentNumber.Length > 0
            && !documentNumber.Equals("Đơn bán hàng", StringComparison.OrdinalIgnoreCase))
        {
            y += Space(3f, scale);
            y += DrawBlock(graphics, documentNumber, smallFont, margin, y, contentWidth, StringAlignment.Center, draw);
        }

        y += Space(10f, scale);
        DrawHorizontalLine(graphics, linePen, margin, y, width - margin, draw);
        y += Space(12f, scale);

        foreach (var meta in payload.Meta ?? [])
        {
            var label = Clean(meta.Label);
            var value = Clean(meta.Value);
            if (label.Length == 0 && value.Length == 0) continue;
            y += DrawLabeledValue(
                graphics,
                label,
                value,
                bodyFont,
                bodyBoldFont,
                margin,
                y,
                contentWidth,
                draw);
            y += Space(4f, scale);
        }

        if ((payload.Meta?.Count ?? 0) > 0)
            y += Space(10f, scale);

        var rows = payload.Rows ?? [];
        for (var index = 0; index < rows.Count; index += 1)
        {
            var values = RowValues(payload.Columns ?? [], rows[index] ?? []);
            var product = First(values, "Sản phẩm", "Tên sản phẩm", "Hàng hóa / SKU");
            if (product.Length == 0)
                product = string.Join(" · ", (rows[index] ?? []).Select(Clean).Where(value => value.Length > 0));

            var productParts = product
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (productParts.Length > 0)
                y += DrawBlock(graphics, productParts[0], productFont, margin, y, contentWidth, StringAlignment.Near, draw);

            for (var partIndex = 1; partIndex < productParts.Length; partIndex += 1)
            {
                y += Space(1f, scale);
                y += DrawBlock(graphics, productParts[partIndex], smallFont, margin, y, contentWidth, StringAlignment.Near, draw);
            }

            y += Space(6f, scale);

            var detailParts = new[]
            {
                First(values, "SL", "Số lượng"),
                First(values, "ĐVT", "Đơn vị tính"),
                First(values, "Đơn giá")
            }.Where(value => value.Length > 0).ToArray();
            var detail = string.Join(" · ", detailParts);
            var lineTotal = First(values, "Thành tiền", "Tổng");

            if (detail.Length > 0 || lineTotal.Length > 0)
            {
                var totalWidth = lineTotal.Length == 0
                    ? 0f
                    : graphics.MeasureString(lineTotal, bodyBoldFont, int.MaxValue, StringFormat.GenericTypographic).Width;
                var gap = 16f * scale;
                var leftWidth = Math.Max(1f, contentWidth - totalWidth - gap);
                var detailWidth = detail.Length == 0
                    ? 0f
                    : graphics.MeasureString(detail, bodyFont, int.MaxValue, StringFormat.GenericTypographic).Width;

                if (lineTotal.Length > 0 && detail.Length > 0 && leftWidth >= contentWidth * 0.46f && detailWidth <= leftWidth)
                {
                    var detailHeight = DrawBlock(graphics, detail, bodyFont, margin, y, leftWidth, StringAlignment.Near, draw);
                    var totalHeight = DrawBlock(
                        graphics,
                        lineTotal,
                        bodyBoldFont,
                        margin + contentWidth - totalWidth - 2f,
                        y,
                        totalWidth + 2f,
                        StringAlignment.Far,
                        draw);
                    y += Math.Max(detailHeight, totalHeight);
                }
                else
                {
                    if (detail.Length > 0)
                        y += DrawBlock(graphics, detail, bodyFont, margin, y, contentWidth, StringAlignment.Near, draw);
                    if (lineTotal.Length > 0)
                    {
                        y += Space(2f, scale);
                        y += DrawBlock(graphics, lineTotal, bodyBoldFont, margin, y, contentWidth, StringAlignment.Far, draw);
                    }
                }
            }

            y += Space(12f, scale);
            if (index < rows.Count - 1)
            {
                DrawHorizontalLine(graphics, itemPen, margin, y, width - margin, draw);
                y += Space(12f, scale);
            }
        }

        var totals = payload.Totals ?? [];
        if (totals.Count > 0)
        {
            y += Space(8f, scale);
            DrawHorizontalLine(graphics, linePen, margin, y, width - margin, draw);
            y += Space(15f, scale);

            for (var index = 0; index < totals.Count; index += 1)
            {
                var total = totals[index];
                var label = Clean(total.Label);
                var value = Clean(total.Value);
                var emphasis = index == totals.Count - 1
                               || label.Equals("Tổng cộng", StringComparison.OrdinalIgnoreCase)
                               || label.Equals("TỔNG CỘNG", StringComparison.OrdinalIgnoreCase);
                var labelFont = emphasis ? totalLabelFont : bodyFont;
                var valueFont = emphasis ? totalValueFont : bodyBoldFont;

                var valueWidth = graphics.MeasureString(value, valueFont, int.MaxValue, StringFormat.GenericTypographic).Width;
                var gap = 14f * scale;
                var labelWidth = Math.Max(1f, contentWidth - valueWidth - gap);
                var labelHeight = DrawBlock(graphics, label, labelFont, margin, y, labelWidth, StringAlignment.Near, draw);
                var valueHeight = DrawBlock(
                    graphics,
                    value,
                    valueFont,
                    margin + contentWidth - valueWidth - 2f,
                    y,
                    valueWidth + 2f,
                    StringAlignment.Far,
                    draw);
                y += Math.Max(labelHeight, valueHeight);
                y += Space(emphasis ? 12f : 7f, scale);
            }
        }

        var footer = payload.Footer ?? [];
        if (footer.Count > 0)
        {
            y += Space(6f, scale);
            DrawHorizontalLine(graphics, itemPen, margin, y, width - margin, draw);
            y += Space(12f, scale);
            foreach (var line in footer)
            {
                y += DrawBlock(graphics, Clean(line), smallFont, margin, y, contentWidth, StringAlignment.Center, draw);
                y += Space(3f, scale);
            }
        }

        return y + Space(34f, scale);
    }

    private static float DrawLabeledValue(
        Graphics graphics,
        string label,
        string value,
        Font labelFont,
        Font valueFont,
        float x,
        float y,
        float width,
        bool draw)
    {
        if (label.Length == 0)
            return DrawBlock(graphics, value, valueFont, x, y, width, StringAlignment.Near, draw);
        if (value.Length == 0)
            return DrawBlock(graphics, label, labelFont, x, y, width, StringAlignment.Near, draw);

        var labelText = label + " ";
        var labelWidth = graphics.MeasureString(labelText, labelFont, int.MaxValue, StringFormat.GenericTypographic).Width;
        var valueWidth = graphics.MeasureString(value, valueFont, int.MaxValue, StringFormat.GenericTypographic).Width;

        if (labelWidth + valueWidth <= width)
        {
            var labelHeight = DrawBlock(graphics, labelText, labelFont, x, y, labelWidth + 2f, StringAlignment.Near, draw);
            var valueHeight = DrawBlock(graphics, value, valueFont, x + labelWidth, y, width - labelWidth, StringAlignment.Near, draw);
            return Math.Max(labelHeight, valueHeight);
        }

        var first = DrawBlock(graphics, label, labelFont, x, y, width, StringAlignment.Near, draw);
        var second = DrawBlock(graphics, value, valueFont, x, y + first + 2f, width, StringAlignment.Near, draw);
        return first + 2f + second;
    }

    private static float DrawBlock(
        Graphics graphics,
        string value,
        Font font,
        float x,
        float y,
        float width,
        StringAlignment alignment,
        bool draw)
    {
        var text = Clean(value);
        if (text.Length == 0) return 0f;

        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = alignment,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.None
        };
        var measured = graphics.MeasureString(text, font, new SizeF(Math.Max(1f, width), 10000f), format);
        var height = Math.Max(font.GetHeight(graphics), (float)Math.Ceiling(measured.Height));
        if (draw)
        {
            graphics.DrawString(
                text,
                font,
                Brushes.Black,
                new RectangleF(x, y, Math.Max(1f, width), height + 2f),
                format);
        }
        return height;
    }

    private static Dictionary<string, string> RowValues(
        IReadOnlyList<string> columns,
        IReadOnlyList<string> row)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < row.Count; index += 1)
        {
            var label = index < columns.Count ? Clean(columns[index]) : $"Cột {index + 1}";
            if (label.Length > 0) result[label] = Clean(row[index]);
        }
        return result;
    }

    private static string First(IReadOnlyDictionary<string, string> values, params string[] labels)
    {
        foreach (var label in labels)
        {
            if (values.TryGetValue(label, out var value) && !string.IsNullOrWhiteSpace(value))
                return Clean(value);
        }
        return "";
    }

    private static Font Font(float size, FontStyle style) =>
        new("Arial", Math.Max(9f, size), style, GraphicsUnit.Pixel);

    private static float Space(float value, float scale) => Math.Max(1f, value * scale);

    private static string Clean(string? value) => (value ?? "")
        .Replace('\u00A0', ' ')
        .Replace('\u202F', ' ')
        .Trim();

    private static void DrawHorizontalLine(
        Graphics graphics,
        Pen pen,
        float left,
        float y,
        float right,
        bool draw)
    {
        if (draw) graphics.DrawLine(pen, left, y, right, y);
    }
}
