namespace V6Meter.App.Ui;

/// <summary>DataGridView styled as a flat list: no vertical lines, roomy rows, rates in the line's accent color.</summary>
internal sealed class ProcessGrid : DataGridView
{
    public const string ShareColumn = "share";
    public const string V4RateColumn = "v4rate";
    public const string V6RateColumn = "v6rate";

    public ProcessGrid()
    {
        DoubleBuffered = true;
        ReadOnly = true;
        AllowUserToAddRows = false;
        AllowUserToDeleteRows = false;
        AllowUserToResizeRows = false;
        RowHeadersVisible = false;
        SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        MultiSelect = false;
        BorderStyle = BorderStyle.None;
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        EnableHeadersVisualStyles = false;
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        AutoGenerateColumns = false;
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        Font = new Font(Theme.UiFamily, 9.5f);
        ColumnHeadersDefaultCellStyle.Font = new Font(Theme.UiFamily, 8.75f, FontStyle.Bold);
        StandardTab = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        var p = Theme.Current;
        int pad = LogicalToDeviceUnits(10);
        BackgroundColor = p.Card;
        GridColor = p.Border;
        ColumnHeadersHeight = LogicalToDeviceUnits(36);
        RowTemplate.Height = LogicalToDeviceUnits(34);
        foreach (DataGridViewRow r in Rows)
            r.Height = RowTemplate.Height;

        var h = ColumnHeadersDefaultCellStyle;
        h.BackColor = h.SelectionBackColor = p.Card;
        h.ForeColor = h.SelectionForeColor = p.TextSecondary;
        h.Padding = new Padding(pad, 0, pad, 0);

        var d = DefaultCellStyle;
        d.BackColor = p.Card;
        d.ForeColor = p.Text;
        d.SelectionBackColor = p.Selection;
        d.SelectionForeColor = p.Text;
        d.Padding = new Padding(pad, 0, pad, 0);

        Theme.ApplyControlTheme(this);
        Invalidate();
    }

    protected override void OnCellFormatting(DataGridViewCellFormattingEventArgs e)
    {
        base.OnCellFormatting(e);
        if (e.ColumnIndex < 0)
            return;
        var p = Theme.Current;
        string prop = ((DataGridViewTextBoxColumn)Columns[e.ColumnIndex]).DataPropertyName;
        switch (prop, e.Value)
        {
            case (V4RateColumn or V6RateColumn, long v):
                e.Value = v == 0 ? "" : V6Meter.Core.Formatting.ByteFormat.Rate(v);
                // Active rates in the line's accent color.
                e.CellStyle!.ForeColor = e.CellStyle.SelectionForeColor = prop == V4RateColumn ? p.V4 : p.V6;
                e.FormattingApplied = true;
                break;
            case (ShareColumn, double share):
                e.Value = share < 0 ? "–" : $"{share:F0}%";
                e.FormattingApplied = true;
                break;
            case (_, long v):
                e.Value = V6Meter.Core.Formatting.ByteFormat.Size(v);
                e.FormattingApplied = true;
                break;
        }
    }

    /// <summary>Shown centered when there are no rows.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "";

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (RowCount > 0 || EmptyText.Length == 0)
            return;
        var area = new Rectangle(0, ColumnHeadersHeight, Width, Math.Max(0, Height - ColumnHeadersHeight));
        TextRenderer.DrawText(e.Graphics, EmptyText, Font, area, Theme.Current.TextSecondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }
}
