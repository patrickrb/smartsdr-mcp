// ****************************************************************************
///*!	\file ALEComposite.cs
// *	\brief Contains ALE composite interface
// *
// *	\copyright	Copyright 2020 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2023-07-12
// *	\author Jessica Temte
// */
// ****************************************************************************

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

using Flex.Smoothlake.FlexLib.Mvvm;

namespace Flex.Smoothlake.FlexLib
{
    public class ALEComposite : ObservableObject
    {
        private Radio _radio;
        private ALE2G _ale2g;
        private ALE3G _ale3g;
        private ALE4G _ale4g;

        public ALEComposite(Radio radio)
        {
            _radio = radio;
            _ale2g = _radio.ALE2G;
            _ale3g = _radio.ALE3G;
            _ale4g = _radio.ALE4G;
        }

        #region Composite Paths

        private ALECompositePath _compositePath = null;
        public ALECompositePath CompositePath
        {
            get { return _compositePath; }
        }

        private List<ALECompositePath> _compositePathList = new List<ALECompositePath>();
        public List<ALECompositePath> CompositePathList
        {
            get
            {
                if (_compositePathList == null) return null;
                lock (_compositePathList)
                    return _compositePathList;
            }
        }

        public delegate void ALECompositePathAddedEventHandler(ALECompositePath composite_path);
        public event ALECompositePathAddedEventHandler ALECompositePathAdded;

        private void OnALECompositePathAdded(ALECompositePath composite_path)
        {
            if (ALECompositePathAdded != null)
                ALECompositePathAdded(composite_path);
        }

        public delegate void ALECompositePathRemovedEventHandler(ALECompositePath composite_path);
        public event ALECompositePathRemovedEventHandler ALECompositePathRemoved;

        private void OnALECompositePathRemoved(ALECompositePath composite_path)
        {
            if (ALECompositePathRemoved != null)
                ALECompositePathRemoved(composite_path);
        }

        private void RemoveCompositePath(string id)
        {
            ALECompositePath composite_path_to_be_removed = null;
            lock (_compositePathList)
            {
                foreach (ALECompositePath composite_path in _compositePathList)
                {
                    if (composite_path.ID == id)
                    {
                        composite_path_to_be_removed = composite_path;
                        break;
                    }
                }

                if (composite_path_to_be_removed != null)
                    _compositePathList.Remove(composite_path_to_be_removed);
            }

            if (composite_path_to_be_removed != null)
                OnALECompositePathRemoved(composite_path_to_be_removed);
        }

        private void RemoveSubPaths(string id)
        {
            _ale2g.Remove2GPath(id);
            _ale3g.Remove3GPath(id);
            _ale4g.Remove4GPath(id);
        }

        #endregion

        #region Scan Lists

        /// <summary>
        /// List of all scan lists (scan list objects have a name and a list object of type ALECompositePath)
        /// </summary>
        private List<ALEScanList> _scanLists = new List<ALEScanList>();
        public List<ALEScanList> ScanLists
        {
            get
            {
                if (_scanLists == null) return null;
                lock (_scanLists)
                    return _scanLists;
            }
        }

        public delegate void ALEScanListAddedEventHandler(ALEScanList scan_list);
        public event ALEScanListAddedEventHandler ALEScanListAdded;

        private void OnALEScanListAdded(ALEScanList scan_list)
        {
            if (ALEScanListAdded != null)
                ALEScanListAdded(scan_list);
        }

        public delegate void ALEScanListRemovedEventHandler(ALEScanList scan_list);
        public event ALEScanListRemovedEventHandler ALEScanListRemoved;

        private void OnALEScanListRemoved(ALEScanList scan_list)
        {
            if (ALEScanListRemoved != null)
                ALEScanListRemoved(scan_list);
        }

        private void RemoveScanList(string name)
        {
            ALEScanList scan_list_to_be_removed = null;
            lock (_scanLists)
            {
                foreach (ALEScanList scan_list in _scanLists)
                {
                    if (scan_list.Name == name)
                    {
                        scan_list_to_be_removed = scan_list;
                        break;
                    }
                }

                if (scan_list_to_be_removed != null)
                    _scanLists.Remove(scan_list_to_be_removed);
            }

            if (scan_list_to_be_removed != null)
                OnALEScanListRemoved(scan_list_to_be_removed);
        }

        #endregion

        #region Configuration

        /// <summary>
        /// List of all ale configurations
        /// </summary>
        private List<ALEConfiguration> _configurationList = new List<ALEConfiguration>();
        public List<ALEConfiguration> ConfigurationList
        {
            get
            {
                if (_configurationList == null) return null;
                lock (_configurationList)
                    return _configurationList;
            }
        }

        public delegate void ALEConfigurationAddedEventHandler(ALEConfiguration configuration);
        public event ALEConfigurationAddedEventHandler ALEConfigurationAdded;

        private void OnALEConfigurationAdded(ALEConfiguration configuration)
        {
            if (ALEConfigurationAdded != null)
                ALEConfigurationAdded(configuration);
        }

        public delegate void ALEConfigurationRemovedEventHandler(ALEConfiguration configuration);
        public event ALEConfigurationRemovedEventHandler ALEConfigurationRemoved;

        private void OnALEConfigurationRemoved(ALEConfiguration configuration)
        {
            if (ALEConfigurationRemoved != null)
                ALEConfigurationRemoved(configuration);
        }

        private void RemoveConfiguration(string type)
        {
            ALEConfiguration configuration_to_be_removed = null;
            lock (_configurationList)
            {
                foreach (ALEConfiguration configuration in _configurationList)
                {
                    if (configuration.Type == type)
                    {
                        configuration_to_be_removed = configuration;
                        break;
                    }
                }

                if (configuration_to_be_removed != null)
                    _configurationList.Remove(configuration_to_be_removed);
            }

            if (configuration_to_be_removed != null)
                OnALEConfigurationRemoved(configuration_to_be_removed);
        }

        #endregion


        internal void ParseStatus(string s)
        {
            string[] words = s.Split(' ');

            switch (words[0])
            {
                case "composite": //composite path id=<ID> frequency=<frequency> 2g_path=<1|0> 3g_path=<1|0> 4g_path=<1|0>
                    {
                        if (words.Length < 2)
                        {
                            Debug.WriteLine("ALEComposite::ParseStatus - composite path: Too few words -- min 2 (" + words + ")");
                            return;
                        }

                        ALECompositePath composite_path = new ALECompositePath();

                        string[] composite_words = words.Skip(2).Take(words.Length - 2).ToArray(); // skip "composite path"

                        foreach (string kv in composite_words)
                        {
                            string[] tokens = kv.Split('=');
                            if (tokens.Length != 2)
                            {
                                Debug.WriteLine("ALEComposite::ParseStatus - composite path: Invalid key/value pair (" + kv + ")");
                                continue;
                            }

                            string key = tokens[0];
                            string value = tokens[1];

                            switch (key.ToLower())
                            {
                                case "id": composite_path.ID = value; break;
                                case "freq": composite_path.Frequency = value; break;
                                case "2g_path":
                                    {
                                        uint temp;
                                        bool b = uint.TryParse(value, out temp);
                                        if (!b)
                                        {
                                            Debug.WriteLine("ALEComposite::ParseStatus - composite path - 2g_path: Invalid key/value pair (" + kv + ")");
                                            continue;
                                        }
                                        composite_path.Is2GPath = Convert.ToBoolean(temp);
                                    }
                                    break;
                                case "3g_path":
                                    {
                                        uint temp;
                                        bool b = uint.TryParse(value, out temp);
                                        if (!b)
                                        {
                                            Debug.WriteLine("ALEComposite::ParseStatus - composite path - 3g_path: Invalid key/value pair (" + kv + ")");
                                            continue;
                                        }
                                        composite_path.Is3GPath = Convert.ToBoolean(temp);
                                    }
                                    break;
                                case "4g_path":
                                    {
                                        uint temp;
                                        bool b = uint.TryParse(value, out temp);
                                        if (!b)
                                        {
                                            Debug.WriteLine("ALEComposite::ParseStatus - composite path - 4g_path: Invalid key/value pair (" + kv + ")");
                                            continue;
                                        }
                                        composite_path.Is4GPath = Convert.ToBoolean(temp);
                                    }
                                    break;
                            }
                        }

                        _compositePath = composite_path;
                        RaisePropertyChanged("CompositePath");

                        // is this a remove status?

                        if (words.Length == 4 && //composite path id=<ID> removed
                            words[3] == "removed" &&
                                words[2].StartsWith("id="))
                        {
                            // yes -- remove the path
                            RemoveCompositePath(composite_path.ID);
                            RemoveSubPaths(composite_path.ID);
                            RaisePropertyChanged("CompositePathList");
                        }
                        else
                        {
                            // no -- add the path
                            lock (_compositePathList)
                            {
                                //if a path already exists, delete the old one to replace with new path object
                                ALECompositePath oldCompositePath = _compositePathList.Find(cp => cp.ID == composite_path.ID);
                                if (oldCompositePath != null)
                                {
                                    RemoveCompositePath(oldCompositePath.ID);
                                }
                                //add the new path
                                _compositePathList.Add(composite_path);
                            }
                            RaisePropertyChanged("CompositePathList");

                            OnALECompositePathAdded(composite_path);
                        }

                    }
                    break;
                case "scan_list": //scan_list name=<name> path=<path>
                    {
                        if (words.Length < 2)
                        {
                            Debug.WriteLine("ALE::ParseStatus - station: Too few words -- min 2 (" + words + ")");
                            return;
                        }

                        ALEScanList scan_list = new ALEScanList();

                        string[] scan_list_words = words.Skip(1).Take(words.Length - 1).ToArray(); // skip the "scan_list"

                        foreach (string kv in scan_list_words)
                        {
                            string[] tokens = kv.Split('=');
                            if (tokens.Length != 2)
                            {
                                Debug.WriteLine("ALEComposite::ParseStatus - scan_list: Invalid key/value pair (" + kv + ")");
                                continue;
                            }

                            string key = tokens[0];
                            string value = tokens[1];

                            switch (key.ToLower())
                            {
                                case "name": scan_list.Name = value; break;
                                case "path":
                                    {
                                        //We need a composite path object to add to this scan list.

                                        //Is there already a composite path in CompositePathList that matches
                                        //the path id coming in?

                                        ALECompositePath found_composite_path = CompositePathList.Find(x => x.ID == value);

                                        if (found_composite_path != null)
                                        {
                                            //Yes, a composite path with a matching ID exists.

                                            //Now- Does this scan list already exist?  We check this by
                                            //looking in the list of scan lists (ScanLists) for a scan list with
                                            //the name coming into the parser.

                                            ALEScanList found_scan_list = _scanLists.Find(x => x.Name == scan_list.Name);

                                            if (found_scan_list != null)
                                            {
                                                //Yes, this scan list already exists in ScanLists

                                                //Next- Does the path id coming in already exist in the found scan list?  We
                                                //check this by looking in the scan list's list of composite paths.  (See
                                                //the structure of object type ALEScanList at the bottom of this document.)

                                                ALECompositePath found_path_in_list = found_scan_list.Paths.Find(x => x.ID == value);

                                                if (found_path_in_list != null)
                                                {
                                                    //Yes, this path is already in the scan list's list of paths.

                                                    //Set the new scan list to the found scan list.  We will
                                                    //remove/re-add it to the ScanLists later.  (This process is
                                                    //redudant, but consistent with other lists made from ALE parsers.)

                                                    scan_list = found_scan_list;
                                                }
                                                else
                                                {
                                                    //No, this composite path is not aleady in the scan list.

                                                    //Add the path to the scan_list object.  The scan list object
                                                    //will be added to ScanLists later.

                                                    found_scan_list.Paths.Add(found_composite_path);
                                                    scan_list = found_scan_list;
                                                }
                                            }
                                            else
                                            {
                                                //No, this scan list does not exist in list ScanLists yet.

                                                //Add this path to the object scan_list, which will be added
                                                //to ScanLists later.

                                                scan_list.Paths.Add(found_composite_path);
                                            }
                                        }
                                        else
                                        {
                                            //No, a composite path with a matching id does not exist in CompositePathList.

                                            //Give error message and do nothing.

                                            Debug.WriteLine("ALEComposite::ParseStatus - scan_list: Composite Path Does Not Exist.");
                                        }

                                    }
                                    break;
                            }
                        }

                        // is this a remove status?
                        if (words.Length == 3 && //"scan_list name=<name> removed"
                            words[2] == "removed" &&
                                words[1].StartsWith("name="))
                        {
                            // yes -- remove the scan list
                            RemoveScanList(scan_list.Name);
                            RaisePropertyChanged("ScanLists");
                        }
                        else
                        {
                            // no -- add the scan list
                            lock (_scanLists)
                            {
                                //if scan list already exists, delete the old one to replace with new scan list object
                                ALEScanList oldScanList = _scanLists.Find(sl => sl.Name == scan_list.Name);
                                if (oldScanList != null)
                                {
                                    RemoveScanList(oldScanList.Name);
                                }
                                //add the new scan list
                                _scanLists.Add(scan_list);
                            }
                            RaisePropertyChanged("ScanLists");

                            OnALEScanListAdded(scan_list);
                        }
                    }
                    break;
                case "configuration": //configuration type=<type> name=<name>
                    {
                        if (words.Length < 2)
                        {
                            Debug.WriteLine("ALEComposite::ParseStatus - configuration: Too few words -- min 2 (" + words + ")");
                            return;
                        }

                        ALEConfiguration configuration = new ALEConfiguration();

                        string[] config_words = words.Skip(1).Take(words.Length - 1).ToArray(); // skip the "configuration"

                        foreach (string kv in config_words)
                        {
                            string[] tokens = kv.Split('=');
                            if (tokens.Length != 2)
                            {
                                Debug.WriteLine("ALEComposite::ParseStatus - configuration: Invalid key/value pair (" + kv + ")");
                                continue;
                            }

                            string key = tokens[0];
                            string value = tokens[1];

                            switch (key.ToLower())
                            {
                                case "type": configuration.Type = value; break;
                                case "name": configuration.Name = value; break;
                            }
                        }

                        // is this a remove status?
                        if (words.Length == 3 && //"configuration type=<type> removed"
                            words[2] == "removed" &&
                                words[1].StartsWith("type="))
                        {
                            // yes -- remove the configuration
                            RemoveConfiguration(configuration.Type);
                            RaisePropertyChanged("ConfigurationList");
                        }
                        else
                        {
                            // no -- add the configuration
                            lock (_configurationList)
                            {
                                //if configuration already exists, delete the old one to replace with new configuration object
                                ALEConfiguration oldConfiguration = _configurationList.Find(c => c.Type == configuration.Type);
                                if (oldConfiguration != null)
                                {
                                    RemoveConfiguration(oldConfiguration.Type);
                                }
                                //add the new configuration
                                _configurationList.Add(configuration);
                            }
                            RaisePropertyChanged("ConfigurationList");

                            OnALEConfigurationAdded(configuration);
                        }
                    }
                    break;
            }
        }
    }

    public class ALECompositePath
    {
        public string ID { get; set; }
        public string Frequency { get; set; }
        public bool Is2GPath { get; set; }
        public bool Is3GPath { get; set; }
        public bool Is4GPath { get; set; }
    }

    public class ALEScanList
    {
        public string Name { get; set; }
        public List<ALECompositePath> Paths = new List<ALECompositePath>();
    }
    public class ALEConfiguration
    {
        public string Type { get; set; }
        public string Name { get; set; }
    }
}
