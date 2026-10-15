// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gitxen.Views;

/// <summary>A run of labels as coloured chips.</summary>
public sealed partial class LabelStrip : UserControl
{
    public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(
        nameof(Labels),
        typeof(IReadOnlyList<LabelChip>),
        typeof(LabelStrip),
        new PropertyMetadata(null, OnLabelsChanged));

    public LabelStrip()
    {
        InitializeComponent();
    }

    public IReadOnlyList<LabelChip>? Labels
    {
        get => (IReadOnlyList<LabelChip>?)GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    private static void OnLabelsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((LabelStrip)sender).Chips.ItemsSource = args.NewValue;
}
