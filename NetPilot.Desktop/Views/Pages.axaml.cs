using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace NetPilot.Desktop.Views;

// Code-behind for each page. Deliberately empty: the pages carry no logic of their own. If one
// ever needs some, it belongs on the view model beside the state it reads, not here - which is
// also what keeps these pages portable, since a page with behaviour in code-behind is a page
// that has to be rewritten for the next UI framework.
public partial class DashboardView : UserControl { public DashboardView() => InitializeComponent(); }
public partial class TunnelView : UserControl { public TunnelView() => InitializeComponent(); }
public partial class DnsView : UserControl { public DnsView() => InitializeComponent(); }
public partial class LimiterView : UserControl { public LimiterView() => InitializeComponent(); }
public partial class AdaptersView : UserControl { public AdaptersView() => InitializeComponent(); }
public partial class CapabilitiesView : UserControl { public CapabilitiesView() => InitializeComponent(); }
public partial class DiagnosticsView : UserControl { public DiagnosticsView() => InitializeComponent(); }