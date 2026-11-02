# Lobotomy Modern Engine (LME) v0.7.4

**Lobotomy Corporation** の長時間プレイ時のメモリリーク（クラッシュ）・後半日数のフレームレート低下・バニラ固有の9大バグ・図鑑／ギフト欄の文字縮小問題をまとめて解決する統合エンジン改善MODです。

---

## 📦 配布パッケージの選び方（あり版 / なし版）

GitHub の **Releases**（または `dist/` フォルダ）にて、用途に合わせて **2つのエディション（あり版 / なし版）** を用意しています。

| パッケージ名 | デバッグ・育成緩和機能<br>（1LOB強化・初期装備無双） | おすすめの対象者 |
| :--- | :---: | :--- |
| **なし版（Standard Edition）**<br>`LobotomyModernEngine_v0.7.4_Standard_*.zip` | **OFF（無効）** | **【通常のプレイヤー向け】**<br>バニラのゲームバランスを一切崩さず、**軽量化・メモリリーク修正・バニラ9大バグ修正・図鑑/ギフト文字サイズ改善のみ** を適用したい方 |
| **あり版（DebugPlus Edition）**<br>`LobotomyModernEngine_v0.7.4_DebugPlus_*.zip` | **ON（有効）** | **【周回・検証・時短プレイ向け】**<br>上記すべての改善に加え、**「職員ステータス強化コスト一律 1 LOB」「初期装備（警棒・スーツ）＆懺悔E.G.Oの1撃鎮圧・無敵化」** が最初から有効になっている版 |

> 💡 **補足**: 「なし版（Standard）」と「あり版（DebugPlus）」の中身のプログラムは共通であり、ゲーム画面左上の **稼働ステータスバッジ** または **`F9` 診断画面**（および **`F8` キー**、もしくは `BepInEx/config/com.isaka.lobotonymodernengine.cfg`）からいつでも個別に ON / OFF を切り替えられます。

さらに各エディションには、環境に合わせて以下の2種類があります：
- **`_FullBepInEx.zip`（BepInEx本体同梱フルセット）**: バニラ環境にそのまま解凍するだけで導入完了する初心者向けフルパック。
- **`_PluginOnly.zip`（プラグイン単体版）**: すでに BepInEx 5.x を導入済みの方向け（`BepInEx/plugins/` と `config/` のみ）。

---

## 🚀 導入方法

### A. フルセット版（`_FullBepInEx.zip`）を使う場合
1. Steam のライブラリで『Lobotomy Corporation』を右クリック → **「管理」＞「ローカルファイルを閲覧」** を開きます。
2. `LobotomyCorp.exe` があるフォルダに、ZIPの中身（`BepInEx` フォルダ、`winhttp.dll`、`doorstop_config.ini`）をすべてドラッグ＆ドロップして上書き展開します。
3. ゲームを起動すると自動適用され、画面左上に **`● LME v0.7.4 稼働中`** バッジが表示されます（クリックまたは **`F9` キー** でリアルタイム診断モニターが開きます）。

### B. プラグイン単体版（`_PluginOnly.zip`）を使う場合
1. 導入済みの `BepInEx` フォルダに ZIP 内の `BepInEx` フォルダを上書きコピーしてください。

### アンインストール方法
- `BepInEx/plugins/LobotomyModernEngine.dll` を削除（フルセット導入時は `winhttp.dll` を削除）するだけで、いつでも完全に元のバニラ状態に戻ります。

---

## 🔧 仕組み解説 ＆ 技術監査に基づく安全設計（v0.7.4 硬化仕様）

Lobotomy Corporation は Unity 2017.4（Mono .NET 3.5相当）で動作しており、後半日数や長時間プレイで極端に重くなる主因は「グラフィック性能不足」ではなく **「毎フレームの不要なオブジェクト再生成（GCスパイク）とテクスチャ／マテリアルのネイティブメモリリーク」** にあります。
本MODはゲーム本体のファイルを書き換えず、起動時にメモリ上でメソッドを差し替える（Harmonyパッチ）ことで以下の4つのレイヤーを改善しています。

### 1. メモリリーク根絶・CPU負荷軽減の仕組み（4大技術監査ポイント）

1. **① Spineアトラス＆照準テクスチャのキャッシュと明示的 `Object.Destroy` 所有権管理 (`SpineAtlasCachePatch`, `CursorLeakPatch`)**
   - **バニラの問題**:
     - 職員がアニメーションを切り替えるたびに `WorkerSpriteSetter.ChangeSpineData` から `AtlasUtilities.ToAtlasRegionPMAClone` が呼ばれ、全身約20部位ごとに毎回 `new Material()` と `new Texture2D()`（CPU全ピクセル走査のPMA乗算＋ミップマップ生成）を新規生成し、一度も `UnityEngine.Object.Destroy` を呼ばずに放置していました。
     - また弾丸照準の拡縮でも毎フレーム `WorkerSpriteManager.ScaleTexture` が `new Texture2D` を生成してリークしていました。
   - **所有権とネイティブ解放の設計**:
     - C# の `WeakReference` 単体ではマネージドラッパーがGC回収されても Unity C++ 側のネイティブメモリ（VRAM/RAM）は解放されません。
     - そのため、本MODでは **入力元の `Sprite` / `Texture2D` の生存監視にのみ `WeakReference` を使用** し、生成された `CreatedTexture` と `CreatedMaterial` については **キャッシュエントリ側が強参照と唯一の破棄所有権（Ownership）を保持** します。
     - 入力元スプライトがアンロード・破棄された際（5秒周期の `PruneDeadReferences` およびシーン遷移・終了時の `ClearAllAndDestroy`）には、必ず明示的に **`UnityEngine.Object.Destroy(CreatedTexture)` / `UnityEngine.Object.Destroy(CreatedMaterial)`** を呼び出してネイティブ側のメモリを確実に解放します（解放数は `F9` モニターにリアルタイム表示されます）。
2. **② UI・ポートレート・収容室の「完全Dirtyフラグ（差分更新）」化 (`WorkerPortraitPatch`, `IsolateRoomPatch`, `SefiraPanelPatch`)**
   - **バニラの問題**: 職員ポートレート（`WorkerPortraitSetter.LateUpdate`）、収容室（`IsolateRoom`）、部門パネル（`SefiraPanel`）が、変化がないフレームでも毎フレーム全UIパーツの再構築・`Resources.Load`・`ToString()` を実行し、Canvas メッシュ再計算を引き起こしていました。
   - **古いUIが残らないための完全ハッシュ設計**:
     - `WorkerPortraitPatch` では、バニラの `SetWorker` が参照する **すべての状態変化（①生存・パニック・制御不能・死因タイプ、②武器ID・防具ID・防具見た目ID・武器クラス・武器スプライト、③追加ギフト＆置換ギフトの全IDリストと各ギフトの表示/非表示トグル状態、④目・眉・口・前髪・後ろ髪スプライトのInstanceIDと瞳色・髪色、⑤防具4部位の実スプライト参照、⑥所属部門・セフィラ・勤続ランク腕章、⑦WeaponSetフラグ）** を網羅したハッシュを計算しています。
     - さらに `SetArmor` や `SetCustomizing` が直接呼ばれた場合はキャッシュを即時無効化するため、**装備変更・職員の死亡/パニック・部門移動・ギフト取得/表示切替のどれが発生しても1フレームの遅延もなく即座にUIが更新** されます。
3. **③ `GetAgentList()` 等の `ReadOnlyCollection` キャッシュの安全性 (`WorkerListCachePatch`)**
   - **バニラ実装の検証結果**:
     - `Assembly-CSharp.dll` を逆コンパイルして確認した結果、バニラの `AgentManager.GetAgentList()` / `GetAgentSpareList()` および `OfficerManager.GetOfficerList()` は、元から **`return agentList.AsReadOnly();`（`ReadOnlyCollection<T>`）** を返しています。
     - すなわちバニラ自体が「呼び出し側でのリスト変更を禁止する不変ビュー（変更すると `NotSupportedException` が飛ぶ仕様）」として設計されていますが、`List<T>.AsReadOnly()` は呼ばれるたびに毎回 `new ReadOnlyCollection<T>(this)` のラッパーインスタンスをヒープに新規確保してしまいます。
   - **本MODの仕組み**:
     - 内部の `List<T>` インスタンス参照（`___agentList` 等）に紐づく `ReadOnlyCollection<T>` ラッパーを1つだけキャッシュして返します。`ReadOnlyCollection<T>` は内部リストへの参照ラッパーであるため、バニラ内部で `agentList.Add` / `Remove` が行われても即座に最新の内容が反映され、`InitValues()` 等で内部リスト自体が作り直された場合は `object.ReferenceEquals` 判定によって即座に新しいラッパーへ再生成されます。
4. **④ `TakeDamage` / `TakeDamageWithoutEffect` IL Transpiler の命令列検証 (`TakeDamagePatch`)**
   - **逆コンパイルによる実命令列の確認**:
     - `WorkerModel.TakeDamage(UnitModel, DamageInfo)` および `WorkerModel.TakeDamageWithoutEffect(UnitModel, DamageInfo)` では、R / W / B / P の4属性分岐それぞれ（計8箇所）に以下の完全同一な IL シーケンスが存在します：
       ```cil
       ldarg.0
       ldfld    class System.Collections.Generic.List`1<class BarrierBuf> UnitModel::_barrierBufList
       callvirt instance !0[] class System.Collections.Generic.List`1<class BarrierBuf>::ToArray()
       ```
     - 本MODの Transpiler は、直前の命令が `ldfld UnitModel::_barrierBufList` であることを検証した上で、`callvirt List<BarrierBuf>::ToArray()` を `call TakeDamagePatch::GetBarrierSnapshot(List<BarrierBuf>)` に置換します。
     - スタック上の入出力（`List<BarrierBuf>` を1つ pop し、`BarrierBuf[]` を1つ push）はバニラと100%同一であり、バリアが0枚の通常時（全被弾の95%以上）は静的空配列 `EmptyBarriers` を返してヒープ確保をゼロ化します（バリアが1枚以上ある場合はバニラ通り `list.ToArray()` のスナップショット配列を返します）。
     - 実際に置換された箇所数（`8/8箇所`）は起動時のログおよびゲーム内の `F9` 診断モニターでリアルタイムに確認できます。

---

### 2. バニラ固有の9大バグ修正（個別ON/OFF対応）

バニラのコード記述ミスによって発生していた以下の9つのバグを、本来の仕様通りに動作するよう修正しています（すべて `.cfg` で1項目ずつ個別にON/OFF可能）。

| # | 通称 / 設定キー | バニラのバグ原因 | 本MODでの修正内容 |
| :--- | :--- | :--- | :--- |
| 1 | **いつでもどこでも黄昏バグ**<br>`FixTwilightArmorGlobalBuff` | `BossBirdArmor.GetDamageFactor()` が攻撃時、装備している武器が黄昏武器かチェックせず、黄昏防具を着ていれば**全武器の攻撃力が最大2倍**になっていた（さらに `UnitModel` 側で weapon と armor の null チェック対象を取り違えていた）。 | 武器・防具の null チェックを正し、黄昏武器（ID:200038）での攻撃時のみ防具のダメージ倍率ボーナスが乗るよう修正。 |
| 2 | **プラシーボ効果バグ（ケセド研究）**<br>`FixChesedBulletRefiningResearch` | ケセド部門の弾丸研究「HP、SP弾精製（ID:803）」を取得しても、回復量判定側が未登録の文字列キー `"upgrade_recover_bullet"` を参照しており、回復量が25→40に増えていなかった。 | 研究ID `803` の取得状態を正しく判定し、HP/SP弾の回復量が40になるよう修正。 |
| 3 | **マルクトあげませんバグ**<br>`FixMalkuthBossMovementReward` | マルクトのコア抑制クリア報酬「職員の移動速度が増加」に対応する処理が `AgentModel.GetMovementBufBySefiraAbility()` に実装し忘れられていた。 | マルクトコア抑制クリア時に移動速度ボーナス（+10）が正しく加算されるよう修正。 |
| 4 | **溶ける愛ヤンデレ・ツンデレバグ**<br>`FixSlimeGirlBugs` | ①親愛職員が洞察作業で普通/悪いに終わった際、感染職員(`item`)ではなく親愛職員(`agent`)のバフを参照していたため感染者が変異しなかった。<br>②感染者が50%を超えた際のカウンター減少判定の分母が「収容部門の生存職員数」ではなく「施設全体の生存職員数」になっていた。 | ①洞察失敗時に感染職員が正しく変異するよう修正。<br>②収容部門内の生存職員数を分母として50%判定を行うよう修正。 |
| 5 | **壁に向かう女武器「叫び」バグ**<br>`FixLadyLookingAtWallWeapon` | 説明文は「勇気/慎重がランク3未満の場合、50%の確率でSP5減少」だが、コードが `勇気<3 && 慎重<3` かつ **確率100%で 0.1 ダメージ** になっていた。 | 説明文通り `勇気<3 \|\| 慎重<3` のとき **50%の確率で精神力に5ダメージ** 入るよう修正。 |
| 6 | **笑顔（団子）特殊キルバフ不発バグ**<br>`FixDangoWeaponKillBuff` | 特殊攻撃（6連撃）の1撃目で敵が死なないと即座に `skillAvailable = false` にされ、2〜6撃目でトドメを刺してもキルバフ（体力+3・正義+3）が一生入らなかった。 | 各ヒット直前の生存状態を追跡し、6連撃のどのヒットで倒しても確実にキルバフが付与されるよう修正。 |
| 7 | **一等星は常に嘘つきバグ（蒼星防具）**<br>`FixBlueStarArmorHeal` | 蒼星防具の説明文は「同じ空間にいる職員を回復（HP+SP）」なのに、コード上で `RecoverMental(5f)` しか呼ばれておらずHPが回復しなかった。 | 精神力と同時に `RecoverHP(5f)` も実行するよう修正（女王蜂の胞子デバフ中はバニラ仕様通り除外）。 |
| 8 | **効かないねぇ！大鳥だから！バグ**<br>`FixBigBirdOverloadImmunity` | 大鳥のXML定義が `<maxWorkCount>0</maxWorkCount>` のため、`GetMaxWorkCount() != 0` で抽選されるクリフォト暴走の対象から永久に除外されていた。 | UI表示（`GetMaxWorkCountView`）は維持したまま、暴走抽選で正しく対象に含まれるよう修正。 |
| 9 | **紅の傷跡短小＆射程表記ズレバグ**<br>`FixRedHoodWeaponRange` | 紅の傷跡（射程4・中距離）が通常攻撃時に共有 `metaInfo.range = 3f`（短距離）で上書きされていた。またインベントリの武器リストだけ射程表記の閾値がズレていた。 | 通常攻撃時の射程を `4f` に維持し、インベントリの射程表記閾値を図鑑と統一。 |

---

### 3. UI・テキスト視認性修正の仕組み

- **エンサイクロペディア（図鑑）・ギフト欄の文字サイズ改善**:
  - アブノーマリティ記録（`CreatureInfoWindow.SetDesc`）を 20pt の読みやすいサイズに固定し、`Canvas.ForceUpdateCanvases()` で正確な高さを算出してから再配置することで段落重なりを防ぎます。
  - ギフト説明欄（`GiftSlot` / `AgentGiftSlot`）で Unity の `BestFit` による極小化（10ptの豆粒文字化）を解除し、15〜16ptでくっきり読めるようにしています。
  - **ローディング画面の座標・レイアウトはバニラを100%維持** し、黄色いラインや背景バーからの位置ズレが起きない安全設計としています。

---

### 4. デバッグ・育成緩和機能（「あり版 / DebugPlus」または設定ON時）

- **職員強化コスト一律 1 LOB 化 (`CheapStatUpgrade`)**:
  - 新規雇用時および既存職員のステータス強化（勇気・慎重・自制・正義）に必要な LOB ポイントを、全ランク一律 **「1段階につき 1 LOB」** に変更します（`F9` 画面からゲーム中にワンクリックでON/OFF切替可能）。
- **初期装備（警棒・スーツ）＆ 懺悔 E.G.O の神装備化 (`DebugStarterGearBuff`)**:
  - 警棒（ID:1）と懺悔武器（ID:200009）を ALEPH グレード（ランク補正ペナルティ無効）・射程25・超速射・威力10万〜15万に強化し、攻撃時に**相手の耐性を自動判定して吸収／無効を回避し最大ダメージが通る属性へ動的変換**するため、あらゆる敵を1撃で鎮圧可能です。
  - スーツ（ID:22）と懺悔防具（ID:300009）を全属性 0.001倍（99.9%軽減）にします。
  - ゲーム中 **`F8` キー** または **`F9` 画面のボタン** でいつでもバニラ数値と即座に切り替えられます。

---

## 🤝 BaseMod との相性・共存について

1. **BaseMod と BepInEx の関係**:
   - **BaseMod** はゲーム本体の `LobotomyCorp_Data/Managed/Assembly-CSharp.dll` を書き換え、古い `0Harmony.dll`（Harmony 1.2）を使用します。
   - **本MOD（BepInEx版）** はゲーム本体のDLLを書き換えず、`winhttp.dll` 経由で最新の `HarmonyX`（Harmony 2.x）を読み込みます。
   - フルセット版に同梱されている `BepInEx` には **`HarmonyXInterop`（旧Harmony 1.2 互換変換レイヤー）** が搭載されていますが、環境トラブルを避けるため **クリーンなバニラ環境（BaseModなし）への導入を最も推奨** します。
2. **BaseMod 環境からバニラ＋本MODへ移行する場合（セーブデータ保護機能）**:
   - 本MODには **`MissingDataGuardPatch`（欠損MODデータ自動スキップ保護機能）** が標準搭載されており、**以前 BaseMod で追加幻想体・追加E.G.Oを入れて遊んでいたセーブデータをそのままバニラ＋本MOD環境で読み込んでもクラッシュせず安全にプレイを継続できます**。

---

## 🖥️ 機能しているか一目でわかるゲーム内表示（ミニ稼働バッジ ＆ `F9` 診断モニター）

- **左上ミニ稼働バッジ**:
  - 画面左上に **`● LME v0.7.4 稼働中 (41/41パッチ正常) | 60 FPS | 1-LOB:ON | 神装備:ON [F9:詳細]`** が表示され、MODが正常に機能しているかが一目でわかります（不要な場合は `F9` 画面のボタン1つで非表示にできます）。
- **`F9` キー（またはミニバッジをクリック）**:
  - 各パッチの実測カウンタ（Spine再利用数・Destroy解放数・Dirtyスキップ数・Transpiler置換数・バニラバグ阻止回数）と、1-LOB強化・神装備強化のワンクリック切替ボタンを備えた詳細モニターが開きます。
- **`F8` キー**: デバッグ初期装備＆懺悔強化の即時 ON / OFF 切り替え
