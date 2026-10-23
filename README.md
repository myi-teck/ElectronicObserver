
# 七四式電子観測儀 
偉大なる開発版様に、以下の機能を追加した(したい)もの  

## 追加機能

### ダークテーマ
- DockPanelSuite の VS2015 Dark テーマによるダーク化

- 脳筋クラス`ThemeManager` によるイベントフック方式でのパネル内自動配色
  - 既存コードを変更せず、`BackColorChanged` / `ForeColorChanged` イベント監視で動的にテーマを維持
  - 点滅表示（工廠完了、入渠完了等）の文字色も背景に連動して自動調整
  - HPバー、出撃エリア色等のカスタムコントロールにも対応
- テーマ設定は `Settings\ThemeConfiguration.xml` で定義可能

### メモ帳機能
- メモを残せる機能
  - 実行ディレクトリにmemo.txtを入れておくとそれを読み書き

### レイアウトロック
- フローティングウィンドウ、スプリッター、オートハイドを含むレイアウトロック

### リモートコントローラー（ヘッドレス運用）
- 配信元がモニタ-less / RDP 切断 / 描画停止の状態になった検知と通知・自動復旧措置を備えます
- 運用手順（ダミープラグ・電源設定・display_state banner の意味等）は [HEADLESS-OPERATIONS.md](./HEADLESS-OPERATIONS.md) を参照

偉大なる先駆者達に敬意を込めて  
開発版様  
[https://github.com/dais-k/ElectronicObserver]
