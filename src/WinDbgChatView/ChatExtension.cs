using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Contracts;
using DbgX.Interfaces;
using DbgX.Interfaces.Listeners;
using DbgX.Interfaces.Services;
using DbgX.Interfaces.UI;
using DbgX.Util;

namespace WinDbgChatView;

[Export(typeof(IDbgToolWindow))]
[NamedPartMetadata("OssCopilotChat")]
public sealed class ChatExtension : IDbgToolWindow
{
    private const string RepositoryUrl = "https://github.com/kemats/windbg-uiext-ghcp";

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
        var commands = new ToolWindowCommandList();
        commands.Items.Add(new ToolWindowCommand
        {
            Header = "About this extension",
            Command = new DelegateCommand(() => ShowAboutDialog(view))
        });
        ToolWindowView.SetToolWindowCommands(view, commands);
        return view;
    }

    private static void ShowAboutDialog(FrameworkElement view)
    {
        var informationalVersion = typeof(ChatExtension).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var parts = informationalVersion?.Split('+', 2) ?? [];
        var version = parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0] : "Unknown";
        var commit = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : "Unknown";

        var repositoryLink = new Hyperlink(new Run(RepositoryUrl));
        repositoryLink.Click += (_, _) => Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true });

        var details = new StackPanel { Margin = new Thickness(20) };
        details.Children.Add(new TextBlock { Text = "WinDbg Copilot Chat", FontSize = 18, FontWeight = FontWeights.SemiBold });
        details.Children.Add(new TextBlock { Text = $"Version: {version}", Margin = new Thickness(0, 16, 0, 0) });
        details.Children.Add(new TextBlock { Text = $"Commit: {commit}", Margin = new Thickness(0, 6, 0, 0) });
        details.Children.Add(new TextBlock(repositoryLink) { Margin = new Thickness(0, 12, 0, 0) });

        var closeButton = new Button
        {
            Content = "Close",
            IsDefault = true,
            IsCancel = true,
            MinWidth = 80,
            Margin = new Thickness(0, 20, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        details.Children.Add(closeButton);

        var dialog = new Window
        {
            Title = "About this extension",
            Content = details,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 440,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        closeButton.Click += (_, _) => dialog.Close();
        if (Window.GetWindow(view) is { } owner) dialog.Owner = owner;
        dialog.ShowDialog();
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