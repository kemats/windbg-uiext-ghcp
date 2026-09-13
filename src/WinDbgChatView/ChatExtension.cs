using System.ComponentModel.Composition;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Contracts;
using DbgX.Interfaces;
using DbgX.Interfaces.Listeners;
using DbgX.Interfaces.Services;
using DbgX.Interfaces.UI;

namespace WinDbgChatView;

[Export(typeof(IDbgToolWindow))]
[NamedPartMetadata("OssCopilotChat")]
public sealed class ChatExtension : IDbgToolWindow
{
    [Import] public IDbgToolWindowManager ToolWindows { get; set; } = null!;
    [Import] public IDbgEngineSynchronizationContextSource EngineContext { get; set; } = null!;
    [Import] public IDbgConsole Console { get; set; } = null!;
    [Import] public IDbgOutputEvents Output { get; set; } = null!;
    [Import] public IDbgTargetState Target { get; set; } = null!;
    [Import] public IDbgThemeService Theme { get; set; } = null!;
    [Import] public IDbgReporter Reporter { get; set; } = null!;
    private ToolWindowView? _pane;
    private UiAssemblyLoadContext? _loadContext;

    public FrameworkElement GetToolWindowView(object parameter)
    {
        if (_pane is not null) return _pane;
        IChatLogSink log = Reporter is null
            ? NullChatLogSink.Instance
            : new DbgReporterLogSink(Reporter, DbgReporterLogSink.ReadMinimumLevel());
        try
        {
            var root = Path.GetDirectoryName(typeof(ChatExtension).Assembly.Location)!;
            var path = Path.Combine(root, "WinDbgCopilotChat", "ui", "WinDbgCopilot.UI.dll");
            _loadContext ??= new UiAssemblyLoadContext(path);
            var assembly = _loadContext.LoadFromAssemblyPath(path);
            var content = (FrameworkElement)Activator.CreateInstance(assembly.GetType("WinDbgChatView.ChatPane", true)!,
                EngineContext, Console, Output, Target, Theme, log)!;
            _pane = CreateHostView(content);
            return _pane;
        }
        catch (Exception exception)
        {
            log.Log(ChatLogLevel.Error, nameof(ChatExtension), "Chat pane creation failed", exception);
            return CreateHostView(new TextBlock { Text = exception.GetBaseException().Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) });
        }
    }

    private static ToolWindowView CreateHostView(FrameworkElement content)
    {
        var view = new ToolWindowView
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        ToolWindowView.SetTabTitle(view, new ToolWindowTitle("Copilot Chat"));
        ToolWindowView.SetIsWindowPersisted(view, true);
        return view;
    }
}

internal sealed class NullChatLogSink : IChatLogSink
{
    internal static NullChatLogSink Instance { get; } = new();
    public ChatLogLevel MinimumLevel => ChatLogLevel.None;
    public void Log(ChatLogLevel level, string category, string message, Exception? exception = null) { }
}

internal sealed class DbgReporterLogSink(IDbgReporter reporter, ChatLogLevel minimumLevel) : IChatLogSink
{
    public ChatLogLevel MinimumLevel { get; } = minimumLevel;

    public void Log(ChatLogLevel level, string category, string message, Exception? exception = null)
    {
        if (level < MinimumLevel || MinimumLevel == ChatLogLevel.None) return;
        var text = $"[WinDbgCopilotChat] [{category}] {message}";
        if (level >= ChatLogLevel.Error)
        {
            if (exception is null) reporter.Error(false, text);
            else reporter.Error(false, exception, text);
        }
        else if (level >= ChatLogLevel.Warning) reporter.Warning(text);
        else reporter.Info(text);
    }

    internal static ChatLogLevel ReadMinimumLevel()
    {
        var value = Environment.GetEnvironmentVariable("WINDBG_COPILOT_LOG_LEVEL");
        return Enum.TryParse<ChatLogLevel>(value, true, out var level) && Enum.IsDefined(level)
            ? level
            : ChatLogLevel.Information;
    }
}

// Separate MEF parts avoid the duplicate Name metadata supplied by the two metadata attributes.
[Export(typeof(IDbgRibbonTab))]
[RibbonTabMetadata("OssCopilotRibbonTab", int.MinValue)]
public sealed class ChatRibbon : IDbgRibbonTab
{
    [Import] public IDbgToolWindowManager ToolWindows { get; set; } = null!;
    private Control? _tab;

    public Control Tab
    {
        get
        {
            if (_tab is not null) return _tab;
            var button = new Fluent.Button
            {
                Header = "Chat", ToolTip = "Open Copilot Chat", SizeDefinition = "Large",
                LargeIcon = new TextBlock
                {
                    Text = "\uE8F2", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 32, HorizontalAlignment = HorizontalAlignment.Center
                }
            };
            ((TextBlock)button.LargeIcon).SetBinding(TextBlock.ForegroundProperty,
                new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = button });
            System.Windows.Automation.AutomationProperties.SetName(button, "Copilot Chat");
            button.Click += (_, _) => ToolWindows.OpenToolWindow("OssCopilotChat");
            var group = new Fluent.RibbonGroupBox { Header = "Copilot" };
            group.Items.Add(button);
            var tab = new Fluent.RibbonTabItem { Header = "Copilot" };
            tab.Groups.Add(group);
            return _tab = tab;
        }
    }
}