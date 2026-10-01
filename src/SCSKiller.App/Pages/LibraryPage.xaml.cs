using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using SCSKiller.Core;

namespace SCSKiller.App.Pages;

public sealed partial class LibraryPage : Page
{
    public LibraryVm Vm { get; } = new();

    public LibraryPage()
    {
        InitializeComponent();
        ((CollectionViewSource)Resources["GroupedGames"]).Source = Vm.Groups;
        var refresh = new Coalesced(DispatcherQueue, Vm.Refresh);
        void OnChanged(GameState _) => refresh.Request();
        void OnQueue(QueueItem q) { if (q.Stage != QueueStage.Warming) refresh.Request(); }   // progress ticks change nothing here
        Loaded += (_, _) => { App.Core.GameChanged += OnChanged; App.Core.QueueChanged += OnQueue; Icons.Failed += refresh.Request; Vm.Load(); };
        Unloaded += (_, _) => { App.Core.GameChanged -= OnChanged; App.Core.QueueChanged -= OnQueue; Icons.Failed -= refresh.Request; };
    }

    void OnSearch(AutoSuggestBox box, AutoSuggestBoxTextChangedEventArgs _) => Vm.Filter = box.Text;

    void OnSearchKey(object _, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape || Search.Text.Length == 0) return;
        Search.Text = "";
        e.Handled = true;
    }

    /// <summary>--screenshots: a search typed, and the list scrolled to its last section.</summary>
    public string SearchText { set => Search.Text = value; }
    public void ScrollToEnd() { if (Vm.Games.Count > 0) List.ScrollIntoView(Vm.Groups[^1][^1]); }

    static GameRow RowOf(object sender) => (GameRow)((FrameworkElement)sender).DataContext;

    void OnGameClick(object _, ItemClickEventArgs e) => App.Main.Navigate(typeof(DetailPage), ((GameRow)e.ClickedItem).Id);
    void OnDetails(object sender, RoutedEventArgs _) => App.Main.Navigate(typeof(DetailPage), RowOf(sender).Id);
    void OnSettings(object _, RoutedEventArgs __) => App.Main.Navigate(typeof(SettingsPage));
    void OnPlay(object sender, RoutedEventArgs _) => Vm.Play(RowOf(sender));

    // Shift+click forces re-detection; otherwise it's redone only when an exe, build or SCSKiller changes.
    void OnRefresh(object _, RoutedEventArgs __) =>
        Vm.Rescan(force: Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down));

    // Adding only queues the game: nothing compiles until Compile queue.
    void OnAdd(object sender, RoutedEventArgs _)
    {
        App.Core.Enqueue(RowOf(sender).Id);
        Vm.Refresh();
    }

    void OnAddAll(object _, RoutedEventArgs __) => Vm.AddAllReady();
    void OnAddRecommended(object _, RoutedEventArgs __) => Vm.AddAllRecommended();

    void OnCompileQueue(object _, RoutedEventArgs __)
    {
        App.Core.StartQueue();
        App.Main.Navigate(typeof(QueuePage));
    }

    // Unsupported: say why; an encrypted game can be unlocked with its AES key (stored on this PC only).
    async void OnWhy(object sender, RoutedEventArgs _)
    {
        var row = RowOf(sender);
        var text = new TextBlock { Text = row.Reason, TextWrapping = TextWrapping.Wrap };
        var box = new TextBox { PlaceholderText = "0x followed by 64 hex digits", Header = "AES key", Visibility = row.IsEncrypted ? Visibility.Visible : Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 12, Children = { text, box } };
        if (row.IsEncrypted)
            panel.Children.Insert(1, new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
                "SCSKiller couldn't find this game's key in its exe (protected exes hide it). If you have the key, paste it here: " +
                "it's checked against the game's files and kept on this PC only." });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"Why can't {row.Name} be compiled?", Content = panel, CloseButtonText = "Close",
            PrimaryButtonText = row.IsEncrypted ? "Unlock" : "", DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        bool ok;
        var key = box.Text.Trim();
        try { ok = await Task.Run(() => App.Core.SetEncryptionKey(row.Id, key)); }   // opens the game's files
        catch (Exception ex) { ok = false; text.Text = ex.Message; }
        if (ok) Vm.Rescan();
        else await new ContentDialog { XamlRoot = XamlRoot, Title = "That key didn't work", Content = "It doesn't open this game's files.", CloseButtonText = "OK" }.ShowAsync();
    }

    async void OnRecord(object sender, RoutedEventArgs _)
    {
        var row = RowOf(sender);
        if (!await App.ConfirmAsync(this, $"Record {row.Name}?",
                "SCSKiller adds a small d3d12.dll next to the game that writes down every pipeline it creates. " +
                "Play for about 5 minutes, close the game, then add it to the compile queue. You can remove it any time from the game's page.",
                "Add recorder", ContentDialogButton.Primary)) return;
        try { await Task.Run(() => App.Core.InstallRecorder(row.Id)); }   // file IO and the game's re-evaluation
        catch (Exception ex) { await new ContentDialog { XamlRoot = XamlRoot, Title = "Couldn't add the recorder", Content = ex.Message, CloseButtonText = "OK" }.ShowAsync(); }
        Vm.Refresh();
    }
}
