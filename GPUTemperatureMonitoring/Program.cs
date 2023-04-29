using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Cpu;
using Microsoft.Toolkit.Uwp.Notifications;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using System.Web;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;


namespace GPUTemperatureMonitoring
{
    public class MonitoringSetting
    {
        public string SensorPath { get; set; } = string.Empty;
        public string LINENotifyToken { get; set; } = string.Empty;
        public int MonitoringIntervalMS { get; set; } = 0;
        public int TemperatureThreshold { get; set; } = 0;
        public int FailedNotifyIntervalS { get; set; } = 0;

        public MonitoringSetting(string SensorPath, string LINENotifyToken, int MonitoringIntervalMS, int TemperatureThreshold, int FailedNotifyIntervalS)
        {
            this.SensorPath = SensorPath;
            this.LINENotifyToken = LINENotifyToken;
            this.MonitoringIntervalMS = MonitoringIntervalMS;
            this.TemperatureThreshold = TemperatureThreshold;
            this.FailedNotifyIntervalS = FailedNotifyIntervalS;
        }
    }

    internal class TemperatureSensor : IDisposable
    {
        class UpdateVisitor : IVisitor
        {
            public void VisitComputer(IComputer computer)
            {
                computer.Traverse(this);
            }
            public void VisitHardware(IHardware hardware)
            {
                hardware.Update();
                foreach (IHardware subHardware in hardware.SubHardware) subHardware.Accept(this);
            }
            public void VisitSensor(ISensor sensor) { }
            public void VisitParameter(IParameter parameter) { }
        }

        private Computer mComputer;
        private string mSensorPath;

        public TemperatureSensor(string sensorPath)
        {
            this.mSensorPath = sensorPath;

            mComputer = new Computer
            {
                IsGpuEnabled = true,
            };
            mComputer.Open();
            mComputer.Accept(new UpdateVisitor());
        }

        public void Dispose()
        {
            mComputer.Close();
        }

        public float GetTemperature()
        {
            float temperature = -1.0f;
            foreach (IHardware hardware in mComputer.Hardware)
            {
                hardware.Update();
                foreach (ISensor sensor in hardware.Sensors)
                {
                    //Debug.Print("\tSensor: {0}", sensor.Identifier.ToString());

                    if (sensor.Identifier.ToString() == mSensorPath)
                    {
                        temperature = sensor.Value.GetValueOrDefault();
                        goto LOOPEND;
                    }
                }
            }
        LOOPEND:
            return temperature;
        }
    };

    internal static class Program
    {
        private static MonitoringSetting mSetting;
        private static TemperatureSensor mSensor;
        private static System.Timers.Timer mTimer;
        private static NotifyIcon mIcon;

        /// <summary>
        /// アプリケーションのメイン エントリ ポイントです。
        /// </summary>
        [STAThread]
        static void Main()
        {
            mSetting = ReadSetting();

            using (mSensor = new TemperatureSensor(mSetting.SensorPath))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                CreateNotifyIcon();
                SetTimer();

                OnInit();

                Application.Run();

                mIcon.Dispose();
                mTimer.Dispose();
            }
        }

        static void OnInit()
        {
            new ToastContentBuilder()
                .AddText("GPU温度監視開始", AdaptiveTextStyle.Default)
                .Show();
        }

        static MonitoringSetting ReadSetting()
        {
            MonitoringSetting ret;

            string settingPath = Path.Combine(GetExeDir(), "Setting.json");
            using (FileStream fileStream = File.OpenRead(settingPath))
            {
                using (StreamReader reader = new StreamReader(fileStream, System.Text.Encoding.UTF8))
                {
                    ret = JsonSerializer.Deserialize<MonitoringSetting>(reader.ReadToEnd());
                }
            }

            return ret;
        }

        static string GetExeDir()
        {
            return Path.GetDirectoryName(Application.ExecutablePath);
        }

        private static void LINENotify(string message)
        {
            string LINE_url = "https://notify-api.line.me/api/notify";
            Encoding enc = Encoding.UTF8;
            string payload = "message=" + HttpUtility.UrlEncode(message, enc);

            using (WebClient client = new WebClient())
            {
                client.Encoding = enc;
                client.Headers.Add("Content-Type", "application/x-www-form-urlencoded");
                client.Headers.Add("Authorization", "Bearer " + mSetting.LINENotifyToken);
                client.UploadString(LINE_url, payload);
            }
        }

        private static void SetTimer()
        {
            mTimer = new System.Timers.Timer(mSetting.MonitoringIntervalMS);
            mTimer.Elapsed += OnTimedEvent;
            mTimer.AutoReset = true;
            mTimer.Enabled = true;
        }

        private static void CreateNotifyIcon()
        {
            // 常駐アプリ（タスクトレイのアイコン）を作成
            mIcon = new NotifyIcon();
            mIcon.Icon = new Icon("Icon.ico");
            mIcon.ContextMenuStrip = ContextMenu();
            mIcon.Text = "GPU温度監視";
            mIcon.Visible = true;
        }

        private static ContextMenuStrip ContextMenu()
        {
            // アイコンを右クリックしたときのメニューを返却
            var menu = new ContextMenuStrip();
            menu.Items.Add("終了", null, (s, e) =>
            {
                Application.Exit();
            });
            return menu;
        }

        private static void OnTimedEvent(Object source, ElapsedEventArgs e)
        {
            var temperature = mSensor.GetTemperature();
            Debug.Print("{0}", temperature);
            mIcon.Text = string.Format("GPU温度監視: {0}℃", temperature);
            ChangeState(temperature);
        }

        static bool mIsBeforeOverThreshold = false;
        static TimeSpan mBeforeNotifyOverThresholdTime;
        static object mLockObj = new object();

        static void ChangeState(float temperature)
        {
            lock (mLockObj)
            {
                bool isOverThreshold = temperature > mSetting.TemperatureThreshold;
                try
                {
                    if (isOverThreshold)
                    {
                        if (!mIsBeforeOverThreshold) // 前回OKだったけど今回オーバーした
                        {
                            mBeforeNotifyOverThresholdTime = DateTime.Now.TimeOfDay;
                            OnStartOverThreshold(temperature);
                        }
                        else // 引き続きオーバーした
                        {
                            var diff = DateTime.Now.TimeOfDay - mBeforeNotifyOverThresholdTime;
                            if (diff.TotalSeconds >= mSetting.FailedNotifyIntervalS)
                            {
                                mBeforeNotifyOverThresholdTime = DateTime.Now.TimeOfDay;
                                OnContinueOverThreshold(temperature);
                            }
                        }
                    }
                    else
                    {
                        if (mIsBeforeOverThreshold) // 前回オーバーしたしたけど今回OKだった
                        {
                            OnFixedOverThreshold(temperature);
                        }
                    }
                }
                finally
                {
                    mIsBeforeOverThreshold = isOverThreshold;
                }
            }
        }

        static void OnStartOverThreshold(float temperature)
        {
            mIcon.Icon = SystemIcons.Warning;

            new ToastContentBuilder()
                .AddText("GPU温度警告！", AdaptiveTextStyle.Caption)
                .AddText(string.Format("GPU温度が閾値を超えています！"))
                .AddText(string.Format("{0}℃", temperature))
                .Show();

            string lineMessage = string.Format("警告\nGPU温度が閾値を超えています！\n{0}℃", temperature);
            if (!EventLog.SourceExists("GPUTemperatureMonitoring"))
            {
                EventLog.CreateEventSource("GPUTemperatureMonitoring", "Application");
            }
            EventLog.WriteEntry("GPUTemperatureMonitoring", lineMessage,
                System.Diagnostics.EventLogEntryType.Warning, 0, 0);

            LINENotify(lineMessage);
        }

        static void OnContinueOverThreshold(float temperature)
        {
            mIcon.Icon = SystemIcons.Warning;

            new ToastContentBuilder()
                .AddText("GPU温度警告！", AdaptiveTextStyle.Caption)
                .AddText(string.Format("GPU温度が閾値を超えています！"))
                .AddText(string.Format("{0}℃", temperature))
                .Show();

            string lineMessage = string.Format("警告\nGPU温度が閾値を超えています！\n{0}℃", temperature);
            if (!EventLog.SourceExists("GPUTemperatureMonitoring"))
            {
                EventLog.CreateEventSource("GPUTemperatureMonitoring", "Application");
            }
            EventLog.WriteEntry("GPUTemperatureMonitoring", lineMessage,
                System.Diagnostics.EventLogEntryType.Warning, 0, 1);

            LINENotify(lineMessage);
        }

        static void OnFixedOverThreshold(float temperature)
        {
            mIcon.Icon = new Icon("Icon.ico");

            new ToastContentBuilder()
                .AddText("GPU温度正常化", AdaptiveTextStyle.Default)
                .AddText(string.Format("GPU温度が閾値以下に下がりました。"))
                .AddText(string.Format("{0}℃", temperature))
                .Show();

            string lineMessage = string.Format("GPU温度正常化\nGPU温度が閾値以下に下がりました。\n{0}℃", temperature);
            if (!EventLog.SourceExists("GPUTemperatureMonitoring"))
            {
                EventLog.CreateEventSource("GPUTemperatureMonitoring", "Application");
            }
            EventLog.WriteEntry("GPUTemperatureMonitoring", lineMessage,
                System.Diagnostics.EventLogEntryType.Information, 1, 0);

            LINENotify(lineMessage);
        }
    }
}
