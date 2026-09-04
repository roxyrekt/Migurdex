namespace Migurdex.Cli.Tui;

public abstract class BaseView
{
    public          bool SkipOnBack { get; set; }
    public abstract void Render(ITuiNavigator navigator);
}
