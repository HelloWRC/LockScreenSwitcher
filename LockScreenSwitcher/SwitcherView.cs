using System.Runtime.InteropServices;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media.Imaging;
using WinRT;

namespace LockScreenSwitcher;

internal sealed class SwitcherView
{
    private enum State { Loading, Ready, Applying, Done, Error }

    private readonly Action notifyWorkCompleted;
    private readonly Action updateWindowSize;
    private readonly ListView providers;
    private readonly DataTemplate rowTemplate;
    private readonly Button confirm;
    private readonly ProgressRing progress;
    private readonly FontIcon success;
    private readonly ScrollViewer errorPanel;
    private readonly TextBlock errorText;
    private BitmapImage? missingLogo;
    private Action? completeWork;
    private State state = State.Loading;

    internal Grid Root { get; }
    internal bool IsApplying => state == State.Applying;
    internal bool HasError => state == State.Error;
    internal bool CanReceiveFocus => state is not (State.Loading or State.Applying);

    [DynamicWindowsRuntimeCast(typeof(Grid))]
    [DynamicWindowsRuntimeCast(typeof(ListView))]
    [DynamicWindowsRuntimeCast(typeof(DataTemplate))]
    [DynamicWindowsRuntimeCast(typeof(Button))]
    [DynamicWindowsRuntimeCast(typeof(ProgressRing))]
    [DynamicWindowsRuntimeCast(typeof(FontIcon))]
    [DynamicWindowsRuntimeCast(typeof(ScrollViewer))]
    [DynamicWindowsRuntimeCast(typeof(TextBlock))]
    internal SwitcherView(Action notifyWorkCompleted, Action updateWindowSize)
    {
        this.notifyWorkCompleted = notifyWorkCompleted;
        this.updateWindowSize = updateWindowSize;

        using var reader = new StreamReader(OpenResource("SwitcherView.xaml"));
        Root = (Grid)XamlReader.Load(reader.ReadToEnd());
        rowTemplate = (DataTemplate)Root.Resources["ProviderRow"];
        providers = (ListView)Root.FindName("Providers");
        confirm = (Button)Root.FindName("Confirm");
        progress = (ProgressRing)Root.FindName("Progress");
        success = (FontIcon)Root.FindName("Success");
        errorPanel = (ScrollViewer)Root.FindName("ErrorPanel");
        errorText = (TextBlock)Root.FindName("ErrorText");

        confirm.Click += (_, _) => Confirm();
        providers.SelectionChanged += (_, _) =>
        {
            if (state is State.Done or State.Error)
            {
                ChangeState(State.Ready);
            }
            else
            {
                UpdateControls();
            }
        };
        UpdateControls();
    }

    [DynamicWindowsRuntimeCast(typeof(ListViewItem))]
    internal void Load(uint dpi)
    {
        AddProvider(ProviderCatalog.Default);
        bool lightTheme = Root.ActualTheme == ElementTheme.Light;
        StartWork(async () =>
        {
            string? current = LockScreenSettings.Read();
            var available = await ProviderCatalog.DiscoverAsync(dpi, lightTheme);
            return (current, available);
        }, result =>
        {
            providers.Items.Clear();
            foreach (var provider in result.available)
            {
                AddProvider(provider);
            }

            string current = result.current ?? ProviderCatalog.Default.Aumid;
            foreach (ListViewItem item in providers.Items)
            {
                if (string.Equals((string)item.Tag, current, StringComparison.OrdinalIgnoreCase))
                {
                    providers.SelectedItem = item;
                    providers.ScrollIntoView(item);
                    break;
                }
            }

            ChangeState(State.Ready);
            RestoreFocus();
        });
    }

    [DynamicWindowsRuntimeCast(typeof(ListViewItem))]
    private void RestoreFocus()
    {
        if (state == State.Ready)
        {
            var item = (providers.SelectedItem ?? providers.Items.FirstOrDefault()) as ListViewItem;
            if (item?.Focus(FocusState.Programmatic) != true)
            {
                providers.Focus(FocusState.Programmatic);
            }
        }
        else if (state is State.Done or State.Error)
        {
            confirm.Focus(FocusState.Programmatic);
        }
    }

    [DynamicWindowsRuntimeCast(typeof(Grid))]
    [DynamicWindowsRuntimeCast(typeof(Image))]
    [DynamicWindowsRuntimeCast(typeof(TextBlock))]
    private void AddProvider(LockScreenProvider provider)
    {
        var row = (Grid)rowTemplate.LoadContent();
        ((TextBlock)row.FindName("AppName")).Text = provider.Name;
        ((TextBlock)row.FindName("AppId")).Text = provider.Aumid;
        var image = (Image)row.FindName("Logo");

        try
        {
            using var logo = provider.Logo;
            image.Source = logo is null ? null : DecodeLogo(logo);
        }
        catch (Exception exception) when (exception is ArgumentException or COMException or IOException)
        {
            // A stale package icon should not prevent the app from appearing in the list.
        }

        if (provider.UsePlaceholder)
        {
            image.Source = missingLogo ??= LoadEmbeddedLogo("Assets.MissingAppLogo.png");
        }

        var item = new ListViewItem { Content = row, Tag = provider.Aumid };
        AutomationProperties.SetName(item, $"{provider.Name}，{provider.Aumid}");
        ToolTipService.SetToolTip(item, provider.Aumid);
        providers.Items.Add(item);
    }

    private static BitmapImage LoadEmbeddedLogo(string name)
    {
        using var reader = new BinaryReader(OpenResource(name));
        byte[] bytes = reader.ReadBytes(checked((int)reader.BaseStream.Length));
        using var stream = new InMemoryRandomAccessStream();
        stream.WriteAsync(CryptographicBuffer.CreateFromByteArray(bytes)).AsTask().GetAwaiter().GetResult();
        stream.Seek(0);
        return DecodeLogo(stream);
    }

    private static BitmapImage DecodeLogo(IRandomAccessStream stream)
    {
        var bitmap = new BitmapImage();
        bitmap.SetSource(stream);
        return bitmap;
    }

    [DynamicWindowsRuntimeCast(typeof(ListViewItem))]
    private void Confirm()
    {
        if (!CanReceiveFocus || providers.SelectedItem is not ListViewItem { Tag: string aumid })
        {
            return;
        }

        ChangeState(State.Applying);
        StartWork(() =>
        {
            LockScreenSettings.Apply(aumid);
            return Task.FromResult(true);
        }, _ => ChangeState(State.Done));
    }

    private void StartWork<T>(Func<Task<T>> work, Action<T> completed)
    {
        Task<T> task = Task.Run(work);
        completeWork = () =>
        {
            try
            {
                completed(task.GetAwaiter().GetResult());
            }
            catch (Exception exception)
            {
                ChangeState(State.Error, exception.Message);
            }
        };
        _ = task.ContinueWith(_ => notifyWorkCompleted(),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal void CompletePendingWork()
    {
        var completed = completeWork;
        completeWork = null;
        completed?.Invoke();
    }

    private void ChangeState(State next, string? error = null)
    {
        bool hadError = HasError;
        state = next;
        errorText.Text = error ?? string.Empty;
        UpdateControls();
        if (hadError != HasError)
        {
            updateWindowSize();
        }

        if (next is State.Done or State.Error)
        {
            FrameworkElement status = next == State.Done ? success : errorText;
            (FrameworkElementAutomationPeer.FromElement(status) ??
                FrameworkElementAutomationPeer.CreatePeerForElement(status))?
                .RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            RestoreFocus();
        }
    }

    private void UpdateControls()
    {
        bool busy = !CanReceiveFocus;
        providers.IsEnabled = !busy;
        confirm.IsEnabled = !busy && providers.SelectedIndex >= 0;
        progress.IsActive = busy;
        progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        success.Visibility = state == State.Done ? Visibility.Visible : Visibility.Collapsed;
        errorPanel.Visibility = HasError ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Stream OpenResource(string name) =>
        typeof(SwitcherView).Assembly.GetManifestResourceStream($"LockScreenSwitcher.{name}")
        ?? throw new InvalidOperationException($"缺少界面资源 {name}。");
}
