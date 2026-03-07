//	<file>DialogService.cs</file>
//	<summary> Dialog Service class for showing MessageBoxes</summary>
//
//	<copyright file="DialogService.cs" company="FlexRadio, Inc.">
//  Copyright 2012-2025 FlexRadio, Inc.  All Rights Reserved.
// 	Unauthorized use, duplication or distribution of this software is
// 	strictly prohibited by law.
//  </copyright>
// 
// 	<date>2016-01-15</date>
// 	<author>Abed Haque, AB5ED</author>

using System;
using System.Drawing;
using System.Windows;

namespace Flex.UiWpfFramework.Mvvm;

// ReSharper disable once PartialTypeWithSinglePart
public partial class DialogService : IDialogService
{
    public MessageBoxResult ShowMessageBox(string messageBoxText, string title, MessageBoxButton button,
        MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        return MessageBox.Show(messageBoxText, title, button, icon, defaultResult);
    }

    public (MessageBoxResult Result, bool dontShowAgain) ShowCustomMessageBox(string messageBoxText, string title)
    {
        return (
            MessageBox.Show(messageBoxText, title, MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No), true);
    }

    public MessageBoxResult ShowWarningYesNoBox(string messageBoxText, string title)
    {
        return MessageBox.Show(messageBoxText, title, MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
    }

    public MessageBoxResult ShowUHEBox(Exception exception, string uheTextSimple, string uheTextFull)
    {
        return MessageBox.Show(uheTextFull, "Unhandled Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public MessageBoxResult ShowOkBoxWithHelpLink(string messageBoxText, string url, string keyword)
    {
        var box = new OkBoxWithHelpLink
        {
            Message = messageBoxText,
            BoxIcon = SystemIcons.Error,
            Title = "SmartSDR Error",
            Uri = url,
            Anchor = keyword
        };
        
        box.ShowDialog();

        return MessageBoxResult.OK;
    }

    public MessageBoxResult ShowOkBox(string messageBoxText)
    {
        return MessageBox.Show(messageBoxText);
    }

    public void FireAndForgetOkBox(string message)
    {
        throw new NotImplementedException();
    }
}
