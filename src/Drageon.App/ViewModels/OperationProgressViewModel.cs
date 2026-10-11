namespace Drageon.App.ViewModels;

public sealed class OperationProgressViewModel : ViewModelBase
{
    private readonly List<Scope> _operations = [];
    public string Text => string.Join(" · ", _operations.Select(item => item.Text));
    public bool IsRunning => _operations.Count > 0;

    public Scope Begin(string text)
    {
        var scope = new Scope(this, text);
        _operations.Add(scope); Changed();
        return scope;
    }

    private void Changed() { Raise(nameof(Text)); Raise(nameof(IsRunning)); }

    public sealed class Scope(OperationProgressViewModel owner, string text) : IDisposable
    {
        private readonly string _title = text;
        internal string Text { get; private set; } = text;
        public bool Report(string value) { if (!owner._operations.Contains(this)) return false; Text = _title + " — " + value; owner.Changed(); return true; }
        public void Dispose() { owner._operations.Remove(this); owner.Changed(); }
    }
}
