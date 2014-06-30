﻿/*  Copyright (C) 2014 Colton Manville
    This file is part of CSharpMiner.

    CSharpMiner is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    CSharpMiner is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with CSharpMiner.  If not, see <http://www.gnu.org/licenses/>.*/

using CSharpMiner.Helpers;
using CSharpMiner.Interfaces;
using CSharpMiner.ModuleLoading;
using System;
using System.IO.Ports;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace CSharpMiner.MiningDevice
{
    [DataContract]
    public abstract class UsbMinerBase : MiningDeviceBase, IUSBDeviceSettings
    {
        protected const int defaultPollTime = 50;

        [DataMember(Name = "port")]
        [MiningSetting(ExampleValue = "dev/ttyUSB0", Optional = false, Description = "The port the device is connected to. Linux /dev/tty* and Windows COM*")]
        public string Port { get; set; }

        [DataMember(Name = "poll")]
        [MiningSetting(ExampleValue = "50", Optional = true, Description = "Milliseconds the thread waits before looking for incoming data. A larger value will decrease the processor usage but shares won't be submitted right away.")]
        public int PollFrequency { get; set; }

        [IgnoreDataMember]
        public abstract int BaudRate { get; }

        public bool IsConnected { get; private set; }

        protected Thread listenerThread = null;
        protected SerialPort usbPort = null;
        protected IPoolWork pendingWork = null;

        private bool continueRunning = true;

        public UsbMinerBase(string port, int watchdogTimeout = defaultWatchdogTimeout, int pollFrequency = defaultPollTime) : base(watchdogTimeout)
        {
            this.Port = port;
            this.PollFrequency = pollFrequency;
        }

        protected override void OnDeserializing()
        {
            base.OnDeserialized();

            PollFrequency = defaultPollTime;
            this.IsConnected = false;
        }

        protected override void OnDeserialized()
        {
            base.OnDeserialized();

            if (PollFrequency <= 0)
            {
                PollFrequency = defaultPollTime;
            }
        }

        public override void Load()
        {
            base.Load();

            Task.Factory.StartNew(this.Connect);
        }

        private void Connect()
        {
            this.IsConnected = false;

            StopWatchdogTimer();

            try
            {
                string[] portNames = SerialPort.GetPortNames();

                if (!portNames.Contains(Port))
                {
                    Exception e = new SerialConnectionException(string.Format("{0} is not a valid USB port.", (Port != null ? Port : "null")));

                    LogHelper.LogError(e);

                    throw e;
                }

                try
                {
                    usbPort = new SerialPort(Port, BaudRate);
                    //usbPort.DataReceived += DataReceived; // This works on .NET in windows but not in Mono
                    usbPort.Open();
                    continueRunning = true;
                }
                catch (Exception e)
                {
                    LogHelper.ConsoleLogErrorAsync(string.Format("Error connecting to {0}.", Port));
                    throw new SerialConnectionException(string.Format("Error connecting to {0}: {1}", Port, e), e);
                }

                if (LogHelper.ShouldDisplay(LogVerbosity.Normal))
                {
                    LogHelper.ConsoleLogAsync(string.Format("Successfully connected to {0}.", Port), LogVerbosity.Normal);
                }

                if (this.pendingWork != null)
                {
                    Task.Factory.StartNew(() =>
                        {
                            this.StartWork(pendingWork);
                            pendingWork = null;
                        });
                }

                if(PollFrequency <= 0)
                {
                    PollFrequency = defaultPollTime;
                }

                if (this.listenerThread == null)
                {
                    this.listenerThread = new Thread(new ThreadStart(() =>
                        {
                            try
                            {
                                while (this.continueRunning)
                                {
                                    if (usbPort.BytesToRead > 0)
                                    {
                                        DataReceived(usbPort, null);
                                    }

                                    Thread.Sleep(PollFrequency);
                                }
                            }
                            catch (Exception e)
                            {
                                LogHelper.LogErrorAsync(e);

                                if (this.continueRunning)
                                {
                                    Task.Factory.StartNew(() =>
                                        {
                                            this.Restart();
                                        });
                                }
                            }
                        }));
                    this.listenerThread.Start();
                }

                StartWatchdogTimer();
            }
            catch (Exception e)
            {
                LogHelper.LogErrorAsync(e);

                if (this.continueRunning)
                {
                    this.Restart();
                }
            }

            this.IsConnected = true;

            this.OnConnected();
        }

        public override void Unload()
        {
            this.IsConnected = false;

            base.Unload();

            if (continueRunning)
            {
                continueRunning = false;

                if (usbPort != null && usbPort.IsOpen)
                    usbPort.Close();

                try
                {
                    if (listenerThread != null)
                    {
                        listenerThread.Join(200);
                        listenerThread.Abort();
                    }
                }
                finally
                {
                    listenerThread = null;
                }
            }
        }

        protected virtual void SendCommand(byte[] cmd)
        {
            lock (UsbMinerBase.SerialWriteLock)
            {
                this.usbPort.Write(cmd, 0, cmd.Length);
            }
        }

        protected abstract void DataReceived(object sender, SerialDataReceivedEventArgs e);
    }
}
