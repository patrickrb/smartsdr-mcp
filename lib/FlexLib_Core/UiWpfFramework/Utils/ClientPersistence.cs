// ****************************************************************************
///*!	\file ClientPersistence.cs
// *	\brief Utilities that help with Client storage of data
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2016-12-20
// *	\author Eric Wachsmann, KE5DTO
// */
// ****************************************************************************

using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;


namespace Flex.UiWpfFramework.Utils
{
    public class ClientPersistence
    {
        public static bool CheckSettings(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return false;
            if (!File.Exists(filename)) return false;

            bool was_reset = false;

            try
            {
                ConfigurationManager.OpenExeConfiguration(filename);
                XDocument.Load(filename);
            }
            catch (Exception)
            {
                FileInfo fileInfo = new FileInfo(filename);
                FileSystemWatcher watcher = new FileSystemWatcher(fileInfo.Directory.FullName, fileInfo.Name);

                File.Delete(filename);
                was_reset = true;

                if (File.Exists(filename))
                    watcher.WaitForChanged(System.IO.WatcherChangeTypes.Deleted);
            }

            return was_reset;
        }
    }
}
