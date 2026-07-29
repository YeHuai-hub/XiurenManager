using System.Drawing.Drawing2D;

namespace XiurenDownloader;

internal static class ModernTheme
{
    public static readonly Color Background = Color.FromArgb(242, 245, 247);
    public static readonly Color Surface = Color.White;
    public static readonly Color Ink = Color.FromArgb(31, 42, 51);
    public static readonly Color Muted = Color.FromArgb(96, 112, 123);
    public static readonly Color Border = Color.FromArgb(214, 222, 228);
    public static readonly Color Accent = Color.FromArgb(20, 125, 120);
    public static readonly Color AccentSoft = Color.FromArgb(222, 241, 239);
    public static readonly Color Score = Color.FromArgb(205, 83, 58);
    public static readonly Color Canvas = Color.FromArgb(18, 22, 26);

    public static void Apply(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Ink;
        ApplyChildren(form);
    }

    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Color.FromArgb(232, 237, 240);
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(235, 240, 242);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(235, 240, 242);
        grid.ColumnHeadersHeight = 38;
        grid.RowTemplate.Height = 34;
        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = Ink;
        grid.DefaultCellStyle.SelectionBackColor = AccentSoft;
        grid.DefaultCellStyle.SelectionForeColor = Ink;
        grid.DefaultCellStyle.Padding = new Padding(5, 0, 5, 0);
        grid.RowHeadersVisible = false;
    }

    public static void StyleButton(Button button, bool accent = false, bool score = false)
    {
        if (score)
            button.Tag = "theme-score";
        else if (accent)
            button.Tag = "theme-accent";
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = accent || score ? 0 : 1;
        button.FlatAppearance.BorderColor = Border;
        button.BackColor = score ? Score : accent ? Accent : Surface;
        button.ForeColor = accent || score ? Color.White : Ink;
        button.Cursor = Cursors.Hand;
        button.Padding = new Padding(8, 0, 8, 0);
        button.Font = new Font("Microsoft YaHei UI", 9, accent || score ? FontStyle.Bold : FontStyle.Regular);
    }

    public static void RoundButton(Button button, int radius = 6)
    {
        button.Resize += (_, _) => button.Region = RoundedRegion(button.ClientRectangle, radius);
        button.Region = RoundedRegion(button.ClientRectangle, radius);
    }

    private static void ApplyChildren(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            switch (control)
            {
                case TabPage page:
                    page.BackColor = Background;
                    page.ForeColor = Ink;
                    break;
                case Panel panel when panel.BackColor == SystemColors.Control:
                    panel.BackColor = Background;
                    break;
                case SplitContainer split:
                    split.BackColor = Border;
                    split.Panel1.BackColor = Surface;
                    split.Panel2.BackColor = Surface;
                    break;
                case Button button:
                    StyleButton(
                        button,
                        accent: Equals(button.Tag, "theme-accent"),
                        score: Equals(button.Tag, "theme-score"));
                    RoundButton(button);
                    break;
                case DataGridView grid:
                    StyleGrid(grid);
                    break;
                case TextBox box when !box.Multiline:
                    box.BorderStyle = BorderStyle.FixedSingle;
                    box.BackColor = Surface;
                    box.ForeColor = Ink;
                    break;
                case ComboBox combo:
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.BackColor = Surface;
                    combo.ForeColor = Ink;
                    break;
                case NumericUpDown number:
                    number.BorderStyle = BorderStyle.FixedSingle;
                    number.BackColor = Surface;
                    number.ForeColor = Ink;
                    break;
                case Label label:
                    label.ForeColor = label.ForeColor == Color.Empty ? Ink : label.ForeColor;
                    break;
            }

            if (control.HasChildren)
                ApplyChildren(control);
        }
    }

    private static Region RoundedRegion(Rectangle bounds, int radius)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return new Region(bounds);

        using var path = new GraphicsPath();
        var diameter = Math.Max(2, radius * 2);
        var arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return new Region(path);
    }
}
