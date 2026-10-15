// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Gitxen.Views;

/// <summary>
///  The branch illustration: soft colour washes and slowly drifting branch curves with commit dots.
///  Behind Home's empty state and the start screen.
/// </summary>
/// <remarks>
///  Still, apart from fading in when it appears. A continuous drift measured about 1.4% of the machine
///  for as long as it was on screen, which is a poor trade for decoration. With Windows' animation
///  effects off it simply appears.
/// </remarks>
public sealed partial class BranchArt : UserControl
{
    public BranchArt()
    {
        InitializeComponent();

        Loaded += (_, _) => Arrive();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Arrive());
    }

    private void Arrive()
    {
        if (Visibility == Visibility.Visible && IsLoaded && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            ((Storyboard)Resources["Arrival"]).Begin();
        }
    }
}
