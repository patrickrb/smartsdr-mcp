/* Change History:
 * 
 *    Date     Author   Ver               Description
 * ----------------------------------------------------------------------------
 * 2015-06-05  KE5DTO  1.0.0  Initial Version
 * 2015-06-10  KE5DTO  1.0.1  Fixed radio connection issue
 * 2015-06-10  KE5DTO  1.0.2  Changed CTS and DSR to RadioButtons
 * 2015-06-10  KE5DTO  1.0.3  Added Polarity setting
 * 2015-06-10  KE5DTO  1.0.4  Added persistence to Form location and settings
 * 2015-06-10  KE5DTO  1.0.5  Keep the radio from being stuck in TX when closing
 * 2015-06-10  KE5DTO  1.0.6  Added status indicators for CTS, DSR
 * 2015-06-11  KE5DTO  1.0.7  Enabled RTS and DTR on the serial port 
 * 2015-06-11  KE5DTO  1.0.8  Swapped polarity for sanity
 * ----------------------------------------------------------------------------
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Windows.Forms;

using Flex.Smoothlake.FlexLib;
using Flex.UiWpfFramework.Utils;


namespace ComPortPTT
{
    public partial class Form1 : Form
    {
        private SerialPort _serialPort;
        private Radio _radio;

        public Form1()
        {
            InitializeComponent();

            InitComPortList();

            // restore any saved values
            string com_port = Properties.Settings.Default.ComPort;
            if (comboComPort.Items.Contains(com_port))
                comboComPort.Text = com_port;
            else
                MessageBox.Show("Attempt to set COM port to " + com_port + " failed",
                    "Port Not Found",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

            radCTS.Checked = Properties.Settings.Default.CTS;
            radDSR.Checked = Properties.Settings.Default.DSR;

            comboPolarity.Text = Properties.Settings.Default.Polarity;

            WindowPlacement.SetPlacement(this.Handle, Properties.Settings.Default.WindowPlacement);

            API.ProgramName = "ComPortPTT";
            API.RadioAdded += new API.RadioAddedEventHandler(API_RadioAdded);
            API.RadioRemoved += new API.RadioRemovedEventHandler(API_RadioRemoved);
            API.Init();
        }

        void API_RadioAdded(Radio radio)
        {
            if (_radio == null)
            {
                _radio = radio;
                _radio.Connect();
                UpdateMox();
            }
        }

        void API_RadioRemoved(Radio radio)
        {
            if (radio == _radio)
                _radio = null;
        }

        private void InitComPortList()
        {
            comboComPort.Items.Clear();
            comboComPort.Items.Add("None");

            string[] ports = SerialPort.GetPortNames();
            Array.Sort<string>(ports, delegate(string strA, string strB) // sort the strings in logical order
            {
                try
                {
                    int idA = int.Parse(strA.Substring(3));
                    int idB = int.Parse(strB.Substring(3));

                    return idA.CompareTo(idB);
                }
                catch (Exception)
                {
                    return strA.CompareTo(strB);
                }
            });
            comboComPort.Items.AddRange(ports);

            if (comboComPort.SelectedIndex < 0)
                comboComPort.SelectedIndex = 0;
        }

        private void comboComPort_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_serialPort != null)
            {
                try
                {
                    if(_serialPort.IsOpen)
                        _serialPort.Close();
                }
                catch (Exception)
                { }
                _serialPort.PinChanged -= serial_port_PinChanged;
                _serialPort = null;

                // make sure we don't leave the radio keyed
                if (_radio != null && _radio.Mox)
                    _radio.Mox = false;
            }

            if (comboComPort.Text == "None")
            {
                UpdateStatus();
                return;
            }

            try
            {
                _serialPort = new SerialPort(comboComPort.Text);
                _serialPort.DtrEnable = true;
                _serialPort.RtsEnable = true;
                _serialPort.PinChanged += new SerialPinChangedEventHandler(serial_port_PinChanged);
                _serialPort.Open();
            }
            catch (Exception)
            {
                _serialPort = null;
                MessageBox.Show("Error communicating with COM port (" + comboComPort.Text + ")",
                    "Communication Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                comboComPort.SelectedIndex = 0;
            }

            UpdateMox();
        }

        private Polarity _polarity = Polarity.ActiveLow;
        private enum Polarity
        {
            ActiveLow,
            ActiveHigh
        }

        void serial_port_PinChanged(object sender, SerialPinChangedEventArgs e)
        {
            switch (e.EventType)
            {
                case SerialPinChange.CtsChanged:
                case SerialPinChange.DsrChanged:
                    UpdateMox();
                    break;
            }
        }

        private void UpdateMox()
        {
            if (_radio == null || _serialPort == null) return;

            if (radCTS.Checked)
            {
                switch (_polarity)
                {
                    case Polarity.ActiveLow:
                        _radio.Mox = _serialPort.CtsHolding;
                        break;
                    case Polarity.ActiveHigh:
                        _radio.Mox = !_serialPort.CtsHolding;
                        break;
                }
            }
            else if (radDSR.Checked)
            {
                switch (_polarity)
                {
                    case Polarity.ActiveLow:
                        _radio.Mox = _serialPort.DsrHolding;
                        break;
                    case Polarity.ActiveHigh:
                        _radio.Mox = !_serialPort.DsrHolding;
                        break;
                }
            }

            if (this.InvokeRequired)
                Invoke(new MethodInvoker(UpdateStatus));
            else UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_serialPort == null)
            {
                toolStriplblCTS.BackColor = SystemColors.Control;
                toolStriplblDSR.BackColor = SystemColors.Control;
                return;
            }

            if (_serialPort.CtsHolding)
            {
                if (toolStriplblCTS.BackColor != Color.Red)
                    toolStriplblCTS.BackColor = Color.Red;
            }
            else
            {
                if (toolStriplblCTS.BackColor != SystemColors.Control)
                    toolStriplblCTS.BackColor = SystemColors.Control;
            }

            if (_serialPort.DsrHolding)
            {
                if (toolStriplblDSR.BackColor != Color.Red)
                    toolStriplblDSR.BackColor = Color.Red;
            }
            else
            {
                if (toolStriplblDSR.BackColor != SystemColors.Control)
                    toolStriplblDSR.BackColor = SystemColors.Control;
            }
        }

        private void radCTS_CheckedChanged(object sender, EventArgs e)
        {
            UpdateMox();
        }

        private void radDSR_CheckedChanged(object sender, EventArgs e)
        {
            UpdateMox();
        }

        private void comboPolarity_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboPolarity.Text == "Active Low")
                _polarity = Polarity.ActiveLow;
            else if (comboPolarity.Text == "Active High")
                _polarity = Polarity.ActiveHigh;

            UpdateMox();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            Properties.Settings.Default.ComPort = comboComPort.Text;
            Properties.Settings.Default.CTS = radCTS.Checked;
            Properties.Settings.Default.DSR = radDSR.Checked;
            Properties.Settings.Default.Polarity = comboPolarity.Text;
            Properties.Settings.Default.WindowPlacement = WindowPlacement.GetPlacement(this.Handle);
            Properties.Settings.Default.Save();

            if (_radio != null && _radio.Mox)
                _radio.Mox = false;
        }
    }
}
