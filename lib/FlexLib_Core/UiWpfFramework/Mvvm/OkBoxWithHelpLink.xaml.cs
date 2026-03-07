//	<file>OkBoxWithHelpLink.xaml.cs</file>
//	<summary>An OK button only message box with a help link</summary>
//
//	<copyright file="OkBoxWithHelpLink.xaml.cs" company="FlexRadio, Inc.">
//  Copyright 2012-2025 FlexRadio, Inc.  All Rights Reserved.
// 	Unauthorized use, duplication or distribution of this software is
// 	strictly prohibited by law.
//  </copyright>
// 
// 	<date>2024-11-12T21:20:11+0000</date>
// 	<author>Annaliese McDermond, NH6Z</author>

using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Flex.UiWpfFramework.Mvvm;

internal partial class OkBoxWithHelpLink
{
    #region Properties
    
    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(
            nameof(Message),
            typeof(string),
            typeof(OkBoxWithHelpLink),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsMeasure)
            );

    public string Message
    {
        get => (string) GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }
    
    public static readonly DependencyProperty UriProperty =
        DependencyProperty.Register(
            nameof(Uri),
            typeof(string),
            typeof(OkBoxWithHelpLink)
        );

    public string Uri
    {
        get => (string) GetValue(UriProperty);
        set => SetValue(UriProperty, value);
    }
    
    public static readonly DependencyProperty AnchorProperty =
        DependencyProperty.Register(
            nameof(Anchor),
            typeof(string),
            typeof(OkBoxWithHelpLink)
        );

    public string Anchor
    {
        get => (string) GetValue(AnchorProperty);
        set => SetValue(AnchorProperty, value);
    }
    
    public static readonly DependencyProperty IconImageSourceProperty =
        DependencyProperty.Register(
            nameof(IconImageSource),
            typeof(BitmapSource),
            typeof(OkBoxWithHelpLink),
            new FrameworkPropertyMetadata(
                IconToImageSource(SystemIcons.Information),
                FrameworkPropertyMetadataOptions.AffectsRender)
            );

    public BitmapSource IconImageSource
    {
        get => (BitmapSource) GetValue(IconImageSourceProperty);
        private set => SetValue(IconImageSourceProperty, value);
    }

    private static BitmapSource IconToImageSource(Icon icon)
    {
        var bitmap = icon.ToBitmap();
        IntPtr hBitmap = bitmap.GetHbitmap();
    
        return Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
    }

    public Icon BoxIcon
    {
        set => IconImageSource = IconToImageSource(value);
    }

    #endregion

    #region Constructor

    internal OkBoxWithHelpLink()
    {
        InitializeComponent();
        DataContext = this;
    }

    #endregion

    #region Methods

    private RelayCommand<object>? _okPressedCommand;

    public RelayCommand<object> OkPressedCommand
    {
        get
        {
            return _okPressedCommand ??= new RelayCommand<object>( _ =>
            {
                Close();
            });
        }

        set => _okPressedCommand = value;
    }
    
    private RelayCommand<object>? _helpLinkCommand;

    public RelayCommand<object> HelpLinkCommand
    {
        get
        {
            return _helpLinkCommand ??= new RelayCommand<object>( _ =>
            {
                Process.Start(new ProcessStartInfo($"{Uri}#{Anchor}")
                {
                    UseShellExecute = true
                });
            });
        }

        set => _helpLinkCommand = value;
    }

    #endregion
}