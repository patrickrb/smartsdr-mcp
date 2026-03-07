// ****************************************************************************
///*!	\file IDialogService.cs
// *	\brief Dialog Serivce Interface class
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2016-01-15
// *	\author Abed Haque, AB5ED
// */
// ****************************************************************************

using System;
using System.Windows;
namespace Flex.UiWpfFramework.Mvvm
{
    public interface IDialogService
    {
        MessageBoxResult ShowMessageBox(string messageBoxText, string title, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult);
        MessageBoxResult ShowWarningYesNoBox(string messageBoxText, string title);
        MessageBoxResult ShowUHEBox(Exception exception, string uheTextSimple, string uheTextFull);
        MessageBoxResult ShowOkBox(string messageBoxText);
        (MessageBoxResult Result, bool dontShowAgain) ShowCustomMessageBox(string messageBoxText, string title);
        void FireAndForgetOkBox(string message);
        public MessageBoxResult ShowOkBoxWithHelpLink(string messageBoxText, string url, string keyword);
    }
}
