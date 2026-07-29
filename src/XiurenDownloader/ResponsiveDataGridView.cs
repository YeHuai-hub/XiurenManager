namespace XiurenDownloader;

internal sealed class ResponsiveDataGridView : DataGridView
{
    protected override AccessibleObject CreateAccessibilityInstance()
    {
        return new LightweightGridAccessibleObject(this);
    }

    private sealed class LightweightGridAccessibleObject : ControlAccessibleObject
    {
        private readonly ResponsiveDataGridView owner;

        public LightweightGridAccessibleObject(ResponsiveDataGridView owner) : base(owner)
        {
            this.owner = owner;
        }

        public override string? Name
        {
            get => string.IsNullOrWhiteSpace(owner.AccessibleName) ? "数据列表" : owner.AccessibleName;
            set => owner.AccessibleName = value;
        }

        public override AccessibleRole Role => AccessibleRole.Table;

        public override int GetChildCount() => 0;
    }
}
