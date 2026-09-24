namespace Migurdex.Cli.Tui;

public abstract class BaseView
{
    public          bool SkipOnBack { get; set; }
    public abstract Task RenderAsync(ITuiNavigator navigator);

    public virtual string GetRpcState()
    {
        return "Geziniyor";
    }
}
