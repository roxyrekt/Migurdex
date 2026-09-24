using Migurdex.Cli.Services;
using Spectre.Console;

namespace Migurdex.Cli.Tui;

public interface ITuiNavigator
{
    bool      IsRunning { get; }
    void      Push(BaseView view, bool skipOnBack = false);
    void      Pop();
    void      Replace(BaseView view);
    BaseView? Peek();
    Task      StartAsync(BaseView initialView);
    void      Exit();
}

public class TuiNavigator : ITuiNavigator
{
    private readonly IDiscordRpcService? _rpcService;
    private readonly Stack<BaseView>     _viewStack = new();

    public TuiNavigator(IDiscordRpcService? rpcService = null)
    {
        _rpcService = rpcService;
    }

    public bool IsRunning { get; private set; } = true;

    public async Task StartAsync(BaseView initialView)
    {
        Push(initialView);
        await RunLoopAsync();
    }

    public void Push(BaseView view, bool skipOnBack = false)
    {
        view.SkipOnBack = skipOnBack;
        _viewStack.Push(view);
    }

    public void Pop()
    {
        if (_viewStack.Count > 1)
        {
            _viewStack.Pop();

            while (_viewStack.Count > 1 && _viewStack.Peek().SkipOnBack)
            {
                _viewStack.Pop();
            }
        }
        else
        {
            Exit();
        }
    }

    public void Replace(BaseView view)
    {
        if (_viewStack.Count > 0)
        {
            _viewStack.Pop();
        }

        _viewStack.Push(view);
    }

    public BaseView? Peek()
    {
        return _viewStack.Count > 0 ? _viewStack.Peek() : null;
    }

    public void Exit()
    {
        IsRunning = false;
        _rpcService?.ClearPresence();
    }

    private void UpdateRpc(BaseView view)
    {
        _rpcService?.UpdateNavigationPresence(view.GetRpcState());
    }

    private async Task RunLoopAsync()
    {
        while (IsRunning && _viewStack.Count > 0)
        {
            var currentView = _viewStack.Peek();
            UpdateRpc(currentView);
            try
            {
                await currentView.RenderAsync(this);
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
                AnsiConsole.MarkupLine("[red]Devam etmek için bir tuşa basın...[/]");
                Console.ReadKey(true);
                Pop();
            }
        }
    }
}
