# テスト版インストール時の修正点

## 前提

- 正規版は利用時に Off にする想定です。
- ただし Off はプラグイン内部の動作モードであり、Dalamud 上でプラグイン自体が読み込まれている限り、識別子やコマンドは衝突し得ます。
- 正規版を残したままテスト版を入れる場合は、少なくともプラグイン識別子を分けてください。

## 必須の修正

### 1. プラグイン識別子を変更する

対象ファイル:

- [RotationSolver/RotationSolver.json](RotationSolver/RotationSolver.json)
- [manifest.json](manifest.json)

変更する項目:

- Name
- InternalName

推奨例:

- Name: Rotation Solver Reborn Test
- InternalName: RotationSolverTest

理由:

- 実際のビルドとパッケージ出力で使われる manifest は [RotationSolver/RotationSolver.json](RotationSolver/RotationSolver.json) です。
- ルートの [manifest.json](manifest.json) は自動生成ではなく重複コピーなので、混乱を避けるため同じ内容にそろえておくのが安全です。
- InternalName を変えると、Dalamud 側で正規版と別プラグインとして扱いやすくなります。

### 2. UI 上の表示名を変更する

対象ファイル:

- [RotationSolver/RotationSolverPlugin.cs](RotationSolver/RotationSolverPlugin.cs)

変更する箇所:

- public static string Name => "Rotation Solver Reborn";

推奨例:

- public static string Name => "Rotation Solver Reborn Test";

理由:

- この文字列は WindowSystem の名前や UI 表示に使われます。
- manifest だけ変えても、ウィンドウ名や一部表示が正規版のまま残るのを防げます。

## 正規版をインストールしたまま使う場合の推奨修正

### 3. チャットコマンドを変更する

対象ファイル:

- [RotationSolver.Basic/Service.cs](RotationSolver.Basic/Service.cs)

変更する項目:

- COMMAND
- ALTCOMMAND
- AUTOCOMMAND
- OFFCOMMAND

現状:

- /rotation
- /rsr
- /rotation Auto
- /rotation Off

推奨例:

- /rotationtest
- /rsrt
- /rotationtest Auto
- /rotationtest Off

理由:

- コマンドは [RotationSolver/Commands/RSCommands_BasicInfo.cs](RotationSolver/Commands/RSCommands_BasicInfo.cs) でプラグイン初期化時に登録されます。
- 正規版が Dalamud 上で読み込まれたままだと、内部モードが Off でも同じコマンド名は衝突します。

### 4. コマンド案内文を更新する

対象ファイル:

- [RotationSolver/UI/RotationConfigWindow.cs](RotationSolver/UI/RotationConfigWindow.cs)
- [RotationSolver/UI/FirstStartTutorialWindow.cs](RotationSolver/UI/FirstStartTutorialWindow.cs)

理由:

- コマンド文字列だけ変えると、ヘルプ表示や初回チュートリアルが古い /rotation のまま残ります。
- 利用者向けの案内を新しいコマンド名に合わせるためです。

## 追加修正が原則不要な箇所

### 設定ファイル

- [RotationSolver.Basic/Configuration/Configs.cs](RotationSolver.Basic/Configuration/Configs.cs) は Svc.PluginInterface.ConfigFile を使っています。
- そのため InternalName を変えれば、設定ファイルの保存先は Dalamud 側のプラグイン識別子に追従します。

### IPC 名

- [RotationSolver/IPC/IPCSubscriber.cs](RotationSolver/IPC/IPCSubscriber.cs) は Svc.PluginInterface.InternalName と Manifest.Name を参照しています。
- そのため Name と InternalName を変えれば、IPC 側も基本的には追従します。

## 最小変更セット

### 正規版を無効化またはアンロードして、テスト版だけを読み込む場合

- 1. プラグイン識別子の変更
- 2. UI 表示名の変更

### 正規版をインストールしたまま同居させる場合

- 1. プラグイン識別子の変更
- 2. UI 表示名の変更
- 3. チャットコマンドの変更
- 4. コマンド案内文の更新

## 補足

- [RotationSolver/RotationSolver.json](RotationSolver/RotationSolver.json) だけでなく [manifest.json](manifest.json) も合わせて更新しておくと、リポジトリ内のメタ情報不整合を避けられます。
- 逆に [manifest.json](manifest.json) だけを変えても、ビルド出力に使われる情報は変わりません。