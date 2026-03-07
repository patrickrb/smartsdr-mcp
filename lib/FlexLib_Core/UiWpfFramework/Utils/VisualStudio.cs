// ****************************************************************************
///*!	\file VisualStudio.cs
// *	\brief Utility functions useful in Visual Studio
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2012-03-05
// *	\author Eric Wachsmann, KE5DTO
// */
// ****************************************************************************

using System.ComponentModel;
using System.Diagnostics;

namespace Flex.UiWpfFramework.Utils
{
    public static class VisualStudio
    {
        public static bool IsDesignTime
        {
            get { return LicenseManager.UsageMode == LicenseUsageMode.Designtime; }
        }

        public static bool IsRunningFromIDE
        {
            get
            {
                bool inIDE = Debugger.IsAttached;
                /*string[] args = System.Environment.GetCommandLineArgs();
                if (args != null && args.Length > 0)
                {
                    string prgName = args[0].ToUpper();
                    inIDE = prgName.EndsWith("VSHOST.EXE");
                }*/
                return inIDE;
            }
        }
    }
}
