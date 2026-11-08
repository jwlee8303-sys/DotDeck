using System.Windows;
using System.Windows.Controls;
using DotDeck.Core.Security;

namespace DotDeck.App.Views.Editor;

public partial class SecretValueEditor : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(SecretValueEditor),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
    public static readonly DependencyProperty IsSecretProperty = DependencyProperty.Register(nameof(IsSecret), typeof(bool), typeof(SecretValueEditor),
        new PropertyMetadata(false, Changed));
    private bool _sync;
    private bool _reveal = !SecretPresentation.MaskByDefault;
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsSecret { get => (bool)GetValue(IsSecretProperty); set => SetValue(IsSecretProperty, value); }
    public SecretValueEditor()
    {
        InitializeComponent(); Refresh();
        Loaded += (_, _) => { SecretPresentation.PreferenceChanged += OnPreference; OnPreference(SecretPresentation.MaskByDefault); };
        Unloaded += (_, _) => SecretPresentation.PreferenceChanged -= OnPreference;
    }
    private void OnPreference(bool mask) { _reveal = !mask; Refresh(); }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((SecretValueEditor)sender).Refresh();
    private void Refresh()
    {
        if (Hidden is null) return;
        _sync = true;
        Hidden.Password = Value ?? string.Empty;
        Plain.Text = Value ?? string.Empty;
        Hidden.Visibility = IsSecret && !_reveal ? Visibility.Visible : Visibility.Collapsed;
        Plain.Visibility = IsSecret && !_reveal ? Visibility.Collapsed : Visibility.Visible;
        Eye.Visibility = IsSecret ? Visibility.Visible : Visibility.Collapsed;
        _sync = false;
    }
    private void OnReveal(object sender, RoutedEventArgs e) { _reveal = !_reveal; Refresh(); if (_reveal) Plain.Focus(); else Hidden.Focus(); }
    private void OnPassword(object sender, RoutedEventArgs e) { if (!_sync) SetCurrentValue(ValueProperty, Hidden.Password); }
    private void OnText(object sender, TextChangedEventArgs e) { if (!_sync) SetCurrentValue(ValueProperty, Plain.Text); }
}
