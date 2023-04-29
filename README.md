# GPUTemperatureMonitoring

GPUの温度を監視して閾値を超えたら警告を送るソフト。

警告を送る先
- トースト通知
- イベントログ
- LINE Notify

## 設定内容
Setting.json
- SensorPath: LibreHardwareMonitorLibでのセンサーのパス
- LINENotifyToken: LINE Notifyのトークン
- MonitoringIntervalMS: センサーから値を取得する間隔(ミリ秒)
- TemperatureThreshold: 警告を送る閾値温度
- FailedNotifyIntervalS: 連続して閾値を超えたときに警告を送る間隔(秒)
