//============================================================================================
// remote_connection.js
// WebSocket / HTTP通信・自動再接続・ステータス管理
// 依存: remote_config.js (定数・グローバル変数)
// 2026/08/01 myi 修正: 単一メッセージ受信対応・指数バックオフ・HUD同期・サンプリング検証
//============================================================================================

//============================================================================================
// グローバル描画関数: drawBitmap - bitmap を canvas に描画
//============================================================================================
function drawBitmap(bitmap) {
    const canvas = document.getElementById('game-screen');
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
}

//============================================================================================
// グローバル描画関数: handleWebpFrame - v2 type=0x00 WebP フレームの正規描画経路
// WCS セッションの init フレーム（Type 0x00）は ClientMode 非依存で送付されるため、
// WCS 接続でもこの経路が機能する必要がある（旧サーバー/バージョン混在の防御経路も兼ねる）
//============================================================================================
function handleWebpFrame(frameData, seq) {
    const blob = new Blob([frameData]);
    ProtocolManager.safeCreateImageBitmap(blob).then((bitmap) => {
        if (bitmap) {
            drawBitmap(bitmap);
            // FPS 加算は汎描画プリミティブの drawBitmap 側でなく実描画経路側に置く:
            // 別経路（初期化専用フレーム・静止画再描画等）から呼ばれた場合の水増しを防ぐ
            frameCount++;
            updateFpsDisplay();
            bitmap.close();
        }
    });
}

//============================================================================================
// グローバル通知関数: showTempToast - 数秒表示して自動消える一時トースト
// HTML 静的要素を増やさず、異常時のみ使う通知のため動的生成＋インラインスタイルで最小実装
//============================================================================================
function showTempToast(message, durationMs) {
    const toast = document.createElement('div');
    toast.textContent = message;
    toast.style.cssText = 'position:fixed;top:12px;left:50%;transform:translateX(-50%);z-index:9999;background:rgba(40,40,40,0.92);color:#fff;padding:8px 16px;border-radius:4px;font-size:13px;pointer-events:none;';
    document.body.appendChild(toast);
    setTimeout(() => { toast.remove(); }, durationMs || 5000);
}

//============================================================================================
// ProtocolManager: WebSocket / HTTP の自動切替・フォールバック・再接続を管理
//============================================================================================
const ProtocolManager = {
    MODES: { AUTO: 'auto', WEBSOCKET: 'websocket', HTTP: 'http', WEBCODECS: 'webcodecs' },
    currentMode: null,
    effectiveMode: null,
    wsConnection: null,
    wsReconnectAttempts: 0,
    maxReconnectAttempts: 5,
    reconnectDelayMs: 1000,
    wsHeartbeatTimer: null,
    wsHeartbeatIntervalMs: 15000,
    httpPollingActive: false,

    // サーバー配信 FPS（エンコーダ設定値）。接続時に /capture-config で取得して上書きする。
    // 0 = 未取得（デコーダ側は 30fps 既定値へフォールバック）。
    // デコーダのタイムスタンプ増分はエンコーダ FPS と一致させる必要があり、
    // 実値より狭い間隔だと復元順のクランプ（TS_CLAMP）が頻発する
    webCodecsFps: 0,

    // [A1修正] 意図的 close 抑制フラグ
    // モード切替（setMode → startConnection）など意図的な closeWebSocket() 実行時に true を設定し、
    // 旧 WS の非同期発火する onclose（onWsClose）が reconnectWebSocket() / fallbackToHttp() を
    // トリガーすることを抑制する。これにより 1 クリック = 1 WS = 1 switch_mode送信 が保証される。
    // 異常切断（サーバ側切断・ネットワークエラー）時は false のままで、再接続機構は従来通り動作する。
    _intentionalClose: false,

    // [案B-4] syncFps二重送信防止用
    _lastSyncFpsSendTime: Date.now(),
    _minSyncFpsInterval: 2000,

    // [案C-1] デコードエラーカウンタ + init要求レートリミット
    // [FPS Fix] init要求の最小インターバルを1000ms→3000msに拡大（サーバー負荷抑制）
    _consecutiveDecodeErrors: 0,
    _lastInitRequestTime: 0,
    _minInitInterval: 3000,

    // [FPS Fix] 可観測性: 受信FPSカウント用
    _frameReceiveCount: 0,
    _frameReceiveStartTime: Date.now(),
    _decodeErrorCount: 0,
    _initRequestCount: 0,

    // 内容変化 FPS 計測: 直前 WS-WebP ペイロードのシグネチャ（byteLength＋先頭4B＋末尾4B）と重複数。
    // 中央ループは内容非変化でも目標 FPS で配信するため、受信数と内容実変化数の乖離（二重レート）を可視化する
    _lastFrameSig: null,
    _dupFrameCount: 0,
    
    // [🔍 plan02 1-5] デコード成功FPS計測用
    _decodeSuccessCount: 0,

    // [二次レビュー修正] init要求送信後の保護期間フラグ
    _initProtectionActive: false,
    _initProtectionMs: 5000,

    // [指数バックオフパラメータ] 再接続間隔の指数バックオフ
    _initBackoffMs: 1000,       // 初回再接続間隔: 1秒
    _maxBackoffMs: 10000,       // 最大再接続間隔: 10秒
    _backoffMultiplier: 2,      // 乗数: 2倍
    _currentBackoffMs: 1000,    // 現在のバックオフ間隔
    _jitterRangeMs: 500,        // 乱数ジャITTER範囲: ±0.5秒（同期防止のため）

    // [案E-3] Pong監視用タイマー
    _pongTimeoutId: null,
    _pongTimeoutMs: 30000,

    // ホスト側描画凍結検知用: 直近のバイナリフレーム/ハートビート受信時刻と FROZEN バッジ状態
    _lastBinaryFrameAt: 0,
    _lastHeartbeatAt: 0,
    _frozenSuspected: false,
    _idleHinted: false,
    _frozenWatchdogTimer: null,

    // [HUD同期] init_hud 受信フラグ
    _hudInitialized: false,
    _hudInitTimeout: null,

    // 描画ゲート: HUD 初期化と独立（初回バイナリフレームで開く）。
    // HUD 未到達でも初フレーム＝最新画面の描画をブロックしないための分離ゲート
    _frameGateOpen: false,

    // WCS 接続確立から初 H.264 関連フレーム（0x01/0x02）受信まで true。
    // 黒画面が「初期化中」か「故障」かをユーザーに区別させるバッジの状態
    _wcsWaitingFirstFrame: false,
    
    // [task19 v3] HTTP モード用 HUD 初期化リトライカウンタ
    _hudInitRetryCount: 0,

    // [🔍 plan02 1-5] WebP検証サンプリング用
    _validationCounter: 0,
    _consecutiveSuccess: 0,
    _validationEnabled: true,

    // [🔍 plan02 diag] フレーム間ジッター計測用
    _lastFrameReceiveTime: 0,

    // [🔍 plan03 1-0a] initフレーム vs 通常フレームの切り分け用
    // 直前のinit要求のシーケンス番号（init受信時に更新）
    _lastInitSeq: 0,

    // [🔍 plan03 1-0a] デコード失敗ログの出力頻度抑制用
    // 100フレームに1回出力（ブラウザコンソール汚染防止）
    _plan03FailLogCounter: 0,

    // [🔍 plan02 根本原因対策] デコード処理のシリアライズ用 Promiseチェーン
    // GPUデコードキュー飽和防止: 1つずつcreateImageBitmapを逐次実行（厳格なシリアル化）
    _bitmapDecodeChain: Promise.resolve(),
    
    // [二次レビュー修正] デコードキュー長管理 - GPUメモリ枯渇防止
    _pendingDecodeCount: 0,       // 待機中のデコード処理数
    _maxDecodeQueue: 5,           // キュー長閾値（5フレーム以上で破棄）

    // [🔍 plan02 修正] safeCreateImageBitmap: createImageBitmapをラップし、エラーハンドリングを行う
    // WebPフォーマット統一: サーバーはWebPを送信するため image/webp を使用
    async safeCreateImageBitmap(blob) {
        try {
            if (!blob || blob.size === 0) {
                console.warn('[ProtocolManager] safeCreateImageBitmap: empty blob');
                return null;
            }
            const bitmap = await createImageBitmap(blob);
            return bitmap;
        } catch (e) {
            console.warn(`[ProtocolManager] safeCreateImageBitmap error: ${e.message} (blob size: ${blob?.size ?? 'N/A'})`);
            return null;
        }
    },

    init() {
        this.currentMode = localStorage.getItem('protocolMode') || this.MODES.AUTO;
        this.effectiveMode = null;
        this.updateUIProtocolButtons();
        this.startConnection();
    },

    startConnection() {
        // [A1 fix] 意図的 close 抑制フラグ設定
        // setMode() は currentMode を先に更新してから本関数を呼ぶため、
        // このフラグがなければ旧 WS の非同期 onclose が onWsClose で
        // 更新済みの currentMode を読み取り reconnectWebSocket() をトリガーし、
        // 2 本目の WS と switch_mode の 2 回送信が発生する。
        this._intentionalClose = true;
        this.closeWebSocket();
        this.httpPollingActive = false;

        if (this.currentMode === this.MODES.WEBSOCKET) {
            this.effectiveMode = 'ws';
            this.attemptWebSocket();
        } else if (this.currentMode === this.MODES.AUTO) {
            this.effectiveMode = 'ws';
            this.attemptWebSocket();
        } else if (this.currentMode === this.MODES.WEBCODECS) {
            // [task28 WebCodecs] WebCodecsモード: 制御はWS、画面配信はWebCodecs
            this.effectiveMode = 'ws';
            this.attemptWebSocket();
        } else {
            // HTTPモード
            this.effectiveMode = 'http';
            this.httpPollingActive = true;
            this.updateConnStatus('HTTP', true);
            // [A1 fix] HTTP モードでは新 WS を生成しないため、意図的 close フラグをリセット
            this._intentionalClose = false;
        }
    },

    attemptWebSocket() {
        const portStr = location.port ? ':' + location.port : '';
        const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
        const wsUrl = `${protocol}//${location.hostname}${portStr}/ws`;
        console.log('[ProtocolManager] WebSocket 接続試行中:', wsUrl);
        if (window.__eventLog) window.__eventLog.consoleLog('log', '[ProtocolManager] WebSocket 接続試行中: ' + wsUrl, 'WS_CONNECT');
        try {
            this.wsConnection = new WebSocket(wsUrl);
        } catch (e) {
            console.warn('[ProtocolManager] WebSocket creation failed:', e.message);
            if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] WebSocket creation failed: ' + e.message, 'WS_CREATE_FAIL');
            if (this.currentMode === this.MODES.WEBSOCKET) {
                this.reconnectWebSocket();
            } else if (this.currentMode === this.MODES.AUTO) {
                this.fallbackToHttp();
            }
            return;
        }

        // [A1 fix] 新 WS 生成後に意図的 close フラグをリセット
        // 意図的 close のウィンドウは新 WS のライフサイクル開始で終了。
        // これにより、この新 WS の**異常**切断（接続拒否・サーバ側切断等）時は
        // フラグが false のため onWsClose → reconnectWebSocket が従来通り動作する
        // （再接続機構を壊さない）。
        this._intentionalClose = false;
        this.wsConnection.binaryType = 'arraybuffer';

        this.wsConnection.onopen = () => {
            console.log('[ProtocolManager] WebSocket connected successfully:', wsUrl);
            if (window.__eventLog) window.__eventLog.consoleLog('log', '[ProtocolManager] WebSocket connected successfully: ' + wsUrl, 'WS_OPEN');
            // [A1 fix] 新 WS 確立後に意図的 close フラグをリセット（二重安全: 新 WS の onopen 後リセット）
            this._intentionalClose = false;
            this.wsReconnectAttempts = 0;
            this.effectiveMode = 'ws';
            this.updateConnStatus('WS', true);
            // WebP 退避中は再接続でバッジが 'WS' に上書きされるため、退避状態を再提示する（WCS 再試行まで維持）
            if (this._wcsFallbackBadge) {
                this.updateConnStatus('低遅延モード不可 — WebP に退避中', true);
            }

            // [指数バックオフリセット] 接続成功時にバックオフをリセット
            this._currentBackoffMs = this._initBackoffMs;
            console.log(`[ProtocolManager] Backoff reset to ${this._initBackoffMs}ms`);

            // [HUD同期] 接続成功時にHUD未初期化フラグをリセット
            this._hudInitialized = false;
            // 新セッションでは初フレームで描画ゲートを必ず開き直す
            this._frameGateOpen = false;
            this._wcsWaitingFirstFrame = false;
            // フリーズ検知ウォッチドッグ: 新セッションでは計測を振り出しに戻して起動
            this._lastBinaryFrameAt = 0;
            this._lastHeartbeatAt = Date.now();
            this._idleHinted = false;
            this._clearFrozenBadge();
            this.ensureFrozenWatchdog();
            if (this._hudInitTimeout) {
                clearTimeout(this._hudInitTimeout);
                this._hudInitTimeout = null;
            }

            // 接続をユーザー操作（活動）と扱う — onopen でも lastOperationTime を更新し、
            // getDynamicDelay() に ACT（ユーザー選択値）を返させて新セッションを user 値で開始する。
            // auto-FPS 降格（15/45秒）は接続後 15 秒で既存状態機械が再適用するため省電力動作は維持される
            lastOperationTime = Date.now();
            if (typeof getDynamicDelay === 'function') {
                this.syncFps(getDynamicDelay(), true);
            }

            // [HUD同期] 5秒でinit_hudタイムアウト（強制初期化）
            this._hudInitTimeout = setTimeout(() => {
                if (!this._hudInitialized) {
                    console.error(`[ProtocolManager] init_hud timeout (5s): proceeding without HUD`);
                    // 強制初期化発火をイベントログへ残す（コンソール不可視環境でも追跡可能に）
                    if (window.__eventLog) window.__eventLog.consoleLog('error', '[ProtocolManager] init_hud timeout (5s): proceeding without HUD', 'HUD_GATE_TIMEOUT');
                    this._hudInitialized = true; // フォース初期化
                }
            }, 5000);

            // [FPS Fix] init要求送信を100ms遅延させ、サーバー側の準備を待つ
            // 【二次レビュー Major 修正】 PROTOCOL_VERSION 送信: サーバーにクライアントのバージョンを通知
            // 設計書 §5.3.3 定義: 接続確立後に PROTOCOL_VERSION を送信
            // Version 2 = 7Bヘッダー (Version + Type + Flags + Seq)
            this.sendWsControl({ action: 'PROTOCOL_VERSION', version: 2 });
            console.log('[ProtocolManager] PROTOCOL_VERSION=2 を送信しました');
            if (window.__eventLog) window.__eventLog.consoleLog('log', '[ProtocolManager] PROTOCOL_VERSION=2 を送信しました', 'PROTO_VERSION');

            // サーバーのエンコーダ FPS を取得し、デコーダのタイムスタンプ増分を一致させる。
            // 既定 30fps のままサーバーが低 FPS で配信すると、タイムスタンプ間隔が実時間より
            // 狭くなり復元順のクランプ（TS_CLAMP）が頻発して再生が乱れる
            fetch('/capture-config')
                .then(res => res.ok ? res.json() : Promise.reject(new Error('HTTP ' + res.status)))
                .then(cfg => {
                    const srvFps = parseInt(cfg && cfg.fps, 10);
                    if (!isNaN(srvFps) && srvFps > 0) {
                        ProtocolManager.webCodecsFps = srvFps;
                        console.log(`[ProtocolManager] サーバー配信 FPS を同期しました: ${srvFps}fps`);
                        if (window.__eventLog) window.__eventLog.consoleLog('log', `[ProtocolManager] サーバー配信 FPS を同期しました: ${srvFps}fps`, 'WCS_FPS_SYNC');
                    }
                    // サーバー伝達 codec: codec 解決の最優先情報源（旧サーバーはフィールドなし → null で導出経路へ委ねる）
                    ProtocolManager.serverCodec = (cfg && typeof cfg.codec === 'string' && cfg.codec.length > 0) ? cfg.codec : null;
                    // 接続前に既にホスト側表示状態が悪化している場合（ヘッドレス/RDP 切断等）は即 banner を出す。
                    // 非 degraded では防御的に解除: 表示経路は degraded 分岐のみで、前回セッション（同一ページ内の
                    // 再接続）で表示した banner は display_state の再配信がない限り解除されず残留するため
                    if (cfg && cfg.displayState === 'degraded') {
                        ProtocolManager._showDisplayBanner(cfg.displayReason);
                    } else {
                        ProtocolManager._hideDisplayBanner();
                    }
                    // 受信箱の証跡: capture-config 経由の表示状態を接続ごとに記録（旧サーバーはフィールド欠落 → normal 扱い）
                    if (window.__eventLog) window.__eventLog.consoleLog('log', '[ProtocolManager] capture-config 表示状態受信: state=' + ((cfg && cfg.displayState) || 'normal') + ' reason=' + ((cfg && cfg.displayReason) || ''), 'DISPLAY_STATE');
                    // ダミー映像バッジの初回表示（checkLoginState の 5s ポーリングを待たずに同期）
                    if (typeof setDummyBadge === 'function') {
                        setDummyBadge(!!(cfg && cfg.dummyVideo === true));
                    }
                })
                .catch(err => {
                    // 取得失敗は致命的ではない: 既定値（30fps 相当）で動作を継続する
                    console.warn('[ProtocolManager] /capture-config 取得失敗、FPS は既定値を使用: ' + err.message);
                    if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] /capture-config 取得失敗、FPS は既定値を使用: ' + err.message, 'WCS_FPS_SYNC_FAIL');
                    // 前回セッションの伝達値が残ると現サーバーの実 Level と食い違うため、導出経路へ戻す
                    ProtocolManager.serverCodec = null;
                });

            setTimeout(() => {
                console.log('[ProtocolManager] init キーフレーム要求を送信します');
                ProtocolManager._initRequestCount++;
                console.log(`[ProtocolManager] init 要求カウント: ${ProtocolManager._initRequestCount}`);
                if (window.__eventLog) window.__eventLog.consoleLog('log', `[ProtocolManager] init 要求カウント: ${ProtocolManager._initRequestCount}`, 'INIT_REQ');
                this.sendWsControl({ action: 'init' });
            }, 100);

            this.startHeartbeat();

            // [task19 Bug 1 fix] In the case of WCS mode, request a mode switch to the server after the connection is established
            // Since setMode('webcodecs') → startConnection() → attemptWebSocket() does not send switch_mode,
            // we send it here after the connection is established (onopen). The server executes SetCaptureModeAsync(WebCodecs).
            if (this.currentMode === this.MODES.WEBCODECS) {
                this.sendWsControl({ action: 'switch_mode', mode: 'webcodecs' });
                console.log('[ProtocolManager] WCS mode: sent switch_mode webcodecs');
                if (window.__eventLog) window.__eventLog.consoleLog('log', '[ProtocolManager] WCS mode: sent switch_mode webcodecs', 'SWITCH_MODE');
                // WCS 接続確立〜初 H.264 受信までの黒画面を「故障」ではなく「初期化中」として可視化
                this._wcsWaitingFirstFrame = true;
                this.updateConnStatus('初期化中', true);
            }
        };

        this.wsConnection.onmessage = (event) => {
            if (typeof event.data === 'string') {
                if (typeof updateDataUsage === 'function') {
                    updateDataUsage(event.data.length + WS_FRAME_OVERHEAD);
                }
                if (event.data === 'heartbeat') {
                    ProtocolManager._lastHeartbeatAt = Date.now();
                    if (ProtocolManager._pongTimeoutId) {
                        clearTimeout(ProtocolManager._pongTimeoutId);
                        ProtocolManager._pongTimeoutId = null;
                    }
                    return;
                }
                if (event.data === 'pong') {
                    if (ProtocolManager._pongTimeoutId) {
                        clearTimeout(ProtocolManager._pongTimeoutId);
                        ProtocolManager._pongTimeoutId = null;
                    }
                    return;
                }
                this.processWsControlMessage(event.data);
            } else if (event.data instanceof ArrayBuffer || event.data instanceof Blob) {
                // [単一メッセージ受信] ブラウザのRFC 6455準拠実装により、
                // endOfMessage: trueのメッセージは単一のonmessageイベントとして完結して受信される
                // フラグメンテーション再構築ロジックは一切不要
                this._lastBinaryFrameAt = Date.now();
                this._idleHinted = false;
                this.handleScreenUpdateBinary(event.data);
            }
        };

        this.wsConnection.onerror = () => {
            console.warn('[ProtocolManager] WebSocket error');
            if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] WebSocket error', 'WS_ERROR');
        };

        this.wsConnection.onclose = () => {
            this.onWsClose();
        };
    },

    onWsClose() {
        console.warn('[ProtocolManager] WebSocket closed');
        if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] WebSocket closed', 'WS_CLOSE');
        this.stopHeartbeat();
        if (ProtocolManager._pongTimeoutId) {
            clearTimeout(ProtocolManager._pongTimeoutId);
            ProtocolManager._pongTimeoutId = null;
        }
        ProtocolManager._consecutiveDecodeErrors = 0;
        ProtocolManager._initProtectionActive = false;
        // [HUD同期] タイムアウトクリア
        if (this._hudInitTimeout) {
            clearTimeout(this._hudInitTimeout);
            this._hudInitTimeout = null;
        }
        this.updateConnStatus('DISCONNECTED', false);

        // [A1 fix] 意図的 close（モード切替等）時は再接続をトリガーしない。
        // 新 WS は startConnection() の attemptWebSocket() が生成するため
        // 1 クリック = 1 WS = 1 switch_mode送信 となる。
        // 異常切断（サーバ側切断・ネットワークエラー）時はフラグが false のため、
        // 以下に fall-through して従来通り再接続・フォールバックを行う。
        if (this._intentionalClose) {
            console.log('[ProtocolManager] Intentional close detected: skipping reconnection (suppressed)');
            if (window.__eventLog) window.__eventLog.consoleLog('log', '[ProtocolManager] Intentional close detected: skipping reconnection (suppressed)', 'WS_INTENTIONAL_CLOSE');
            this._intentionalClose = false;
            return;
        }

        if (this.currentMode === this.MODES.AUTO) {
            this.fallbackToHttp();
        } else if (this.currentMode === this.MODES.WEBSOCKET) {
            this.reconnectWebSocket();
        } else if (this.currentMode === this.MODES.WEBCODECS) {
            // [task28 WebCodecs] WebCodecsモード: WebSocket再接続を試みる
            this.reconnectWebSocket();
        }
    },

    reconnectWebSocket() {
        // [A1 fix] Reconnection only occurs on abnormal disconnect (flag is false). Reset for safety.
        this._intentionalClose = false;
        if (this.wsReconnectAttempts >= this.maxReconnectAttempts) {
            console.warn('[ProtocolManager] Max reconnect attempts reached, falling back to HTTP');
            if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] Max reconnect attempts reached, falling back to HTTP', 'WS_MAX_RECONNECT');
            this.fallbackToHttp();
            return;
        }

        // [指数バックオフ] ジャITTER付きの再接続遅延
        const jitter = Math.floor(Math.random() * this._jitterRangeMs) - this._jitterRangeMs / 2;
        const delay = Math.max(0, this._currentBackoffMs + jitter);
        this.wsReconnectAttempts++;
        this.updateConnStatus(`WS RETRY (${this.wsReconnectAttempts})`, false);
        console.warn(`[ProtocolManager] 再接続を試みます (delay=${delay}ms, base=${this._currentBackoffMs}ms, jitter=${jitter}ms)`);
        if (window.__eventLog) window.__eventLog.consoleLog('warn', `[ProtocolManager] 再接続を試みます (delay=${delay}ms, base=${this._currentBackoffMs}ms, jitter=${jitter}ms)`, 'WS_RECONNECT');

        // 次回以降のバックオフ間隔を算出（最大値で飽和）
        this._currentBackoffMs = Math.min(
            this._currentBackoffMs * this._backoffMultiplier,
            this._maxBackoffMs
        );

        setTimeout(() => this.attemptWebSocket(), delay);
    },

    fallbackToHttp() {
        console.log('[ProtocolManager] Falling back to HTTP Polling');
        if (window.__eventLog) window.__eventLog.consoleLog('log', '[ProtocolManager] Falling back to HTTP Polling', 'FALLBACK_HTTP');

        // [task19 v3] 順序を明示: closeWebSocket → effectiveMode → initializeHudViaHttp
        // 1. WebSocket 切断（先に切断し、その後にモード切替）
        // [A1 fix] 意図的 close: 旧 WS の onclose による再接続トリガーを抑制
        this._intentionalClose = true;
        this.closeWebSocket();

        // 2. モード切替
        this.effectiveMode = 'http';
        this.httpPollingActive = true;
        // [A1 fix] HTTP モード切替後に意図的 close フラグをリセット（WS 再接続は発生しない）
        this._intentionalClose = false;

        // [task19 v4] 二次レビュー Minor 2 修正: セッション切替時にリトライカウンタをリセット
        this._hudInitRetryCount = 0;

        // 3. HUD 初期化: 初回 /hud fetch で _hudInitialized を正しく立てる
        if (!this._hudInitialized) {
            this.initializeHudViaHttp();
        }

        this.updateConnStatus('HTTP (FB)', true);
    },

    // [task19 v3] HTTP モード用 HUD 初期化関数（ハック排除 + リトライ追加）
    async initializeHudViaHttp() {
        this._hudInitRetryCount = (this._hudInitRetryCount || 0) + 1;

        try {
            const params = `materialVer=${window.hudVersions?.material ?? -1}&fleetsVer=${window.hudVersions?.fleets ?? -1}&battleVer=${window.hudVersions?.battle ?? -1}&combatVer=${window.hudVersions?.combat ?? -1}&questsVer=${window.hudVersions?.quests ?? -1}&logsVer=${window.hudVersions?.logs ?? -1}`;
            const response = await fetch(`/hud?${params}&t=${Date.now()}`);
            if (response.status === 204) {
                this._hudInitialized = true;
                this._hudInitRetryCount = 0;
                console.log('[ProtocolManager] HTTP HUD initialized (no change)');
                return;
            }
            if (!response.ok) throw new Error(`HUD fetch failed: ${response.status}`);
            const text = await response.text();
            const data = JSON.parse(text);
            if (typeof updateHudData !== 'function') {
                throw new Error('updateHudData function is not defined');
            }
            updateHudData(data);
            this._hudInitialized = true;
            this._hudInitRetryCount = 0;
            console.log('[ProtocolManager] HTTP HUD initialized');
            if (window.__eventLog) window.__eventLog.consoleLog('log', '[ProtocolManager] HTTP HUD initialized', 'HTTP_HUD_INIT');
        } catch (e) {
            console.warn(`[ProtocolManager] HTTP HUD init error: ${e.message}`);
            if (window.__eventLog) window.__eventLog.consoleLog('warn', `[ProtocolManager] HTTP HUD init error: ${e.message}`, 'HTTP_HUD_INIT_ERR');

            if (this._hudInitRetryCount < 3) {
                console.log(`[ProtocolManager] Retrying HUD init (${this._hudInitRetryCount}/3)`);
                setTimeout(() => this.initializeHudViaHttp(), 1000);
            } else {
                console.error('[ProtocolManager] HUD init failed after 3 retries');
                if (window.__eventLog) window.__eventLog.consoleLog('error', '[ProtocolManager] HUD init failed after 3 retries', 'HTTP_HUD_INIT_FAIL');
            }
        }
    },

    closeWebSocket() {
        if (this.wsConnection) {
            // [A1 fix] Root-cause measure: null out the old WS's event handlers before close.
            // Since close() triggers onclose asynchronously, the old WS's onclose could later call
            // onWsClose() → reconnectWebSocket() and generate a second WS.
            // By detaching the handlers before close(), onclose no longer calls onWsClose,
            // so the second WS is not generated (1 click = 1 WS = 1 send).
            // Note: JS is single-threaded, so close() and handler nulling complete within the same tick,
            // and onclose is guaranteed to fire afterward → no race.
            this.wsConnection.onopen = null;
            this.wsConnection.onmessage = null;
            this.wsConnection.onerror = null;
            this.wsConnection.onclose = null;
            try { this.wsConnection.close(); } catch (e) {
                // [A1 fix] Do not swallow exceptions silently: log the cause of close() failure (AGENTS.md NEVER)
                console.warn('[ProtocolManager] WebSocket close() failed:', e && e.message ? e.message : e);
                if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] WebSocket close() failed: ' + (e && e.message ? e.message : e), 'WS_CLOSE_FAIL');
            }
            this.wsConnection = null;
        }
        // [A1 fix] Since the old WS's onclose no longer calls onWsClose, also perform cleanup here.
        // (previously onWsClose was responsible for this). Prevents the old timer from closing the new WS.
        if (ProtocolManager._pongTimeoutId) {
            clearTimeout(ProtocolManager._pongTimeoutId);
            ProtocolManager._pongTimeoutId = null;
        }
        // [A1 fix] Also reset connection state here (previously onWsClose was responsible for this)
        ProtocolManager._consecutiveDecodeErrors = 0;
        ProtocolManager._initProtectionActive = false;
        if (this._hudInitTimeout) {
            clearTimeout(this._hudInitTimeout);
            this._hudInitTimeout = null;
        }
        this.stopHeartbeat();
    },

    startHeartbeat() {
        this.stopHeartbeat();
        this.wsHeartbeatTimer = setInterval(() => {
            if (this.wsConnection && this.wsConnection.readyState === WebSocket.OPEN) {
                if (typeof updateDataUsage === 'function') {
                    updateDataUsage(9 + WS_FRAME_OVERHEAD);
                }
                this.wsConnection.send('heartbeat');
                if (ProtocolManager._pongTimeoutId) {
                    clearTimeout(ProtocolManager._pongTimeoutId);
                }
                ProtocolManager._pongTimeoutId = setTimeout(() => {
                    console.warn('[ProtocolManager] Pong timeout - connection may be dead');
                    if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] Pong timeout - connection may be dead', 'PONG_TIMEOUT');
                    this.closeWebSocket();
                    this.reconnectWebSocket();
                }, ProtocolManager._pongTimeoutMs);
            }
        }, this.wsHeartbeatIntervalMs);
    },

    stopHeartbeat() {
        if (this.wsHeartbeatTimer) {
            clearInterval(this.wsHeartbeatTimer);
            this.wsHeartbeatTimer = null;
        }
    },

    sendWsControl(data) {
        if (this.wsConnection && this.wsConnection.readyState === WebSocket.OPEN) {
            const jsonStr = JSON.stringify(data);
            if (typeof updateDataUsage === 'function') {
                updateDataUsage(jsonStr.length + WS_FRAME_OVERHEAD);
            }
            this.wsConnection.send(jsonStr);
        }
    },

    syncFps(intervalMs, force = false, auto = false) {
        if (!force && typeof ProtocolManager !== 'undefined') {
            const now = Date.now();
            if (ProtocolManager._lastSyncFpsSendTime > 0 && (now - ProtocolManager._lastSyncFpsSendTime < ProtocolManager._minSyncFpsInterval)) {
                return;
            }
        }
        if (typeof ProtocolManager !== 'undefined') {
            ProtocolManager._lastSyncFpsSendTime = Date.now();
        }
        if (this.effectiveMode === 'ws') {
            const payload = { action: 'set_fps', intervalMs: intervalMs };
            // auto=true: 自動間隔調整（getDynamicDelay）由来の送信。サーバー側で手動 FPS 設定を
            // 上書きしないためのフラグ（未指定 = 手動設定として扱う）
            if (auto) payload.auto = true;
            this.sendWsControl(payload);
            console.log('[ProtocolManager] syncFps送信: intervalMs=' + intervalMs + ', force=' + force + (auto ? ', auto' : ''));
            if (window.__eventLog) window.__eventLog.consoleLog('log', '[ProtocolManager] syncFps送信: intervalMs=' + intervalMs + ', force=' + force + (auto ? ', auto' : ''), 'SYNC_FPS');
        }
    },

    processWsControlMessage(jsonStr) {
        try {
            const msg = JSON.parse(jsonStr);
            if (msg.type === 'set_quality') {
                console.log('[ProtocolManager] Server set quality:', msg.quality);
                if (window.__eventLog) window.__eventLog.consoleLog('log', '[ProtocolManager] Server set quality: ' + msg.quality, 'SRV_SET_QUALITY');
                if (typeof msg.quality === 'number') {
                    targetQuality = msg.quality;
                    localStorage.setItem('targetQuality', msg.quality);
                    const buttons = document.querySelectorAll('#quality-group .settings-btn');
                    buttons.forEach(btn => {
                        btn.classList.toggle('active', parseInt(btn.getAttribute('data-quality'), 10) === msg.quality);
                    });
                }
            } else if (msg.type === 'navigate') {
                console.log('[ProtocolManager] Server navigate:', msg.url);
                if (window.__eventLog) window.__eventLog.consoleLog('log', '[ProtocolManager] Server navigate: ' + msg.url, 'SRV_NAVIGATE');
                if (msg.url) {
                    window.location.href = msg.url;
                }
            } else if (msg.type === 'init_hud') {
                // [HUD同期] 初期HUDデータを受信
                console.log('[ProtocolManager] Received init_hud data');
                if (window.__eventLog) window.__eventLog.consoleLog('log', '[ProtocolManager] Received init_hud data', 'SRV_INIT_HUD');
                this._hudInitialized = true;
                // タイムアウトをクリア
                if (this._hudInitTimeout) {
                    clearTimeout(this._hudInitTimeout);
                    this._hudInitTimeout = null;
                }
                if (msg.data && typeof updateHudData === 'function') {
                    updateHudData(msg.data);
                }
            } else if (msg.type === 'capture_mode_fallback') {
                // サーバー側で WebCodecs が利用不可になり配信が WebP へ降格した通知。
                // WCS 描画経路（H.264 デコーダ）で待ち続けるのを防ぎ、WebP 描画経路へ降格する
                console.warn('[ProtocolManager] capture_mode_fallback:', msg.reason);
                if (window.__eventLog) window.__eventLog.consoleLog('warn', 'WebCodecs 利用不可（' + msg.reason + '）→ WebSocket(WebP) に降格', 'MODE_FALLBACK');
                if (this.currentMode === this.MODES.WEBCODECS) {
                    showTempToast('WebCodecs 利用不可 → WebSocket(WebP) に降格しました');
                    this.setMode(this.MODES.WEBSOCKET);
                }
            } else if (msg.type === 'display_state') {
                // ホスト側の表示状態変化（ヘッドレス/RDP 切断/rAF ストール）。degraded は画面上部に banner で常時提示
                // 受信箱の証跡: 全受信を state/reason 付きで統一タグに記録（下の分岐ログは banner 操作の提示として維持）
                if (window.__eventLog) window.__eventLog.consoleLog('log', '[ProtocolManager] display_state 受信: state=' + msg.state + ' reason=' + msg.reason, 'DISPLAY_STATE');
                if (msg.state === 'degraded') {
                    console.warn('[ProtocolManager] display_state degraded:', msg.reason);
                    if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] ホスト側表示状態が悪化しました (reason=' + msg.reason + ')', 'DISPLAY_STATE_DEGRADED');
                    this._showDisplayBanner(msg.reason);
                } else {
                    console.log('[ProtocolManager] display_state normal');
                    if (window.__eventLog) window.__eventLog.consoleLog('log', '[ProtocolManager] ホスト側表示状態が正常に戻りました', 'DISPLAY_STATE_NORMAL');
                    this._hideDisplayBanner();
                }
            }
        } catch (e) {
            console.warn('[ProtocolManager] Invalid control message:', e.message);
            if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] Invalid control message: ' + e.message, 'INVALID_CTRL_MSG');
        }
    },

    // 内容変化 FPS 計測: WS-WebP ペイロードをシグネチャ（byteLength＋先頭4B＋末尾4B）で直前フレームと比較する。
    // 同一シグネチャ＝内容非変化の重複配信と集計する（完全ハッシュではなく軽量近似。比較は受信フレーム単位）
    _trackWebpContentChange(frameData) {
        if (!frameData || frameData.byteLength < 4) return;
        const v = new DataView(frameData);
        const head = v.getUint32(0, false);
        const tail = v.byteLength >= 8 ? v.getUint32(v.byteLength - 4, false) : head;
        const sig = frameData.byteLength + ':' + head + ':' + tail;
        if (sig === this._lastFrameSig) {
            this._dupFrameCount++;
        } else {
            this._lastFrameSig = sig;
        }
    },

    // [二次レビュー修正] シーケンス番号判定のタイミング修正
    // _frameReceiveCount と latestSequenceNumber の更新を受信直後に実行し、
    // Promiseチェーン内での判定が常に最新のシーケンス番号に基づいていることを保証する。
    async handleScreenUpdateBinary(arrayBuffer) {
        if (!arrayBuffer || arrayBuffer.byteLength <= 6) return;

        // 初回バイナリフレーム（init フルフレーム含む）で描画を開始する（HUD 初期化は非ブロッキング）
        // _hudInitialized は HUD パネルの表示状態のみ管理（5秒タイムアウトのセーフティネットは維持）
        if (!this._frameGateOpen) {
            this._frameGateOpen = true;
            if (window.__eventLog) window.__eventLog.consoleLog('log', 'frame gate opened by first frame', 'FRAME_GATE_OPEN');
        }

        // [FPS Fix] 可観測性: フレーム受信カウントを**受信直後**に更新
        // [二次レビュー修正] _frameReceiveCount の更新タイミングを受信直後に移動
        ProtocolManager._frameReceiveCount++;

        // [🔍 plan02 diag] WebSocket受信状態の可視化
        const frameTime = performance.now();
        if (this._lastFrameReceiveTime > 0) {
            const jitter = frameTime - this._lastFrameReceiveTime;
            if (jitter > 50) {
                // 50msを超えるジッターは異常とみなしてログ出力
                console.log(`[plan02-diag] Frame jitter: ${jitter.toFixed(1)}ms (seq=${ProtocolManager._frameReceiveCount})`);
            }
        }
        this._lastFrameReceiveTime = frameTime;

        if (typeof updateDataUsage === 'function') {
            updateDataUsage(arrayBuffer.byteLength + WS_FRAME_OVERHEAD);
        }

        // [FPS Fix] 可観測性: 受信FPS計測
        const now = Date.now();
        const elapsed = (now - ProtocolManager._frameReceiveStartTime) / 1000;
        if (elapsed >= 10) {
            const fps = ProtocolManager._frameReceiveCount / elapsed;
            // [🔍 plan02 1-5] デコード成功FPS / 成功率の出力
            const decodeSuccessFps = ProtocolManager._decodeSuccessCount / elapsed;
            const decodeSuccessRate = ProtocolManager._frameReceiveCount > 0 ? (ProtocolManager._decodeSuccessCount / ProtocolManager._frameReceiveCount * 100) : 0;
            // 内容変化 FPS = (受信数 - 重複数)/窓。受信 FPS との差が「内容を変えない重複配信」量（二重レート）
            const dupCount = ProtocolManager._dupFrameCount;
            const contentFps = (ProtocolManager._frameReceiveCount - dupCount) / elapsed;
            console.log(`[ProtocolManager] 受信FPS: ${fps.toFixed(1)} | 内容変化FPS: ${contentFps.toFixed(1)} (重複: ${dupCount}) | デコード成功FPS: ${decodeSuccessFps.toFixed(1)} | デコード成功率: ${decodeSuccessRate.toFixed(1)}% (フレーム数: ${ProtocolManager._frameReceiveCount}, デコード成功: ${ProtocolManager._decodeSuccessCount}, デコードエラー: ${ProtocolManager._decodeErrorCount}, init要求: ${ProtocolManager._initRequestCount}, キュー長: ${ProtocolManager._pendingDecodeCount})`);
            ProtocolManager._frameReceiveCount = 0;
            ProtocolManager._decodeSuccessCount = 0;
            ProtocolManager._frameReceiveStartTime = now;
            ProtocolManager._decodeErrorCount = 0;
            ProtocolManager._dupFrameCount = 0;
        }

        // [task28] プロトコルヘッダー拡張対応（6B → 7B）
        // Version 2 (7B): Version(1B) + Type(1B) + Flags(1B) + Seq(4B)
        // Version 1 (6B): Version(1B) + Combined(1B) + Seq(4B)
        const view = new DataView(arrayBuffer);
        const version = view.getUint8(0);

        if (version >= 2) {
        	// Version 2: 7B ヘッダー（WebCodecs 対応）
        	const type = view.getUint8(1);
        	const flags = view.getUint8(2);
        	const seq = view.getUint32(3, false);
        
        	if (type === 0x02) {
        		// [task19 v3] SPS/PPS (description) 受信
        		const description = arrayBuffer.slice(7);
        		// [task23 F-1-2] 空 ArrayBuffer（byteLength === 0）を保持しない（空 extradata で H.264 デコーダを初期化すると InvalidStateError）
        		let descChanged = false;
        		if (description && description.byteLength > 0) {
        			// 保持中バイトとの差異を判定（同一バイト列の連続受信では再初期化しない — SPS/PPS 不変ならデコーダ再構築不要）
        			descChanged = !ProtocolManager._videoDecoderDescription || !ProtocolManager._descriptionsEqual(ProtocolManager._videoDecoderDescription, description);
        			ProtocolManager._videoDecoderDescription = description;
        		}
        		console.log('[ProtocolManager] SPS/PPS received, length:', description.byteLength || description.length);
        		if (window.__eventLog) window.__eventLog.consoleLog('log', '[ProtocolManager] SPS/PPS received, length: ' + (description.byteLength || description.length), 'SPS_PPS');
        	
        		if (!ProtocolManager._videoDecoderInitialized) {
        			// 保持中の description を渡与する（ローカル変数は空の可能性があるため、保持値で直前の有効な SPS/PPS を初期化に使う）
        			ProtocolManager.initVideoDecoder(ProtocolManager._videoDecoderDescription);
        		} else if (descChanged) {
        			// description 変更時は完全再初期化（close → 再生成 → configure）で行う。
        			// open デコーダへの直接 configure() は行わない: 仕様上 configure() は必ず [[key chunk required]] を
        			// 立てるため、以降に delta が 1 枚でも到達すると DataError を同期的に投げ、エラーバーストで
        			// リトライ/フォールバック経路を誤発動させる。キーフレーム待ち中は delta を decode() 前に
        			// JS レイヤーでドロップし、DataError を構造的に発生させない
        			ProtocolManager._wcsWaitKeyFrame = true;
        			ProtocolManager._wcsWaitKeyDropCount = 0;
        			ProtocolManager._wcsWaitKeySince = Date.now();
        			const reinitOk = ProtocolManager.initVideoDecoder(description);
        			if (window.__eventLog) window.__eventLog.consoleLog('log', '[ProtocolManager] SPS/PPS 変更によりデコーダを再初期化 (descLen=' + description.byteLength + ' codec=' + ProtocolManager.resolveCodecString(description) + ' waitKey=1 reinit=' + (reinitOk ? 'ok' : 'skip') + ')', 'SPS_PPS_REINIT');
        		}
        		// SPS/PPS 受信はサーバー側エンコーダ稼働の証左 — 「初期化中」バッジを解除する
        		ProtocolManager.clearWcsInitBadge();
        		return;
        	} else if (type === 0x01) {
        		// WebCodecs モード（H.264 エンコード済み）
        		const isKeyFrame = (flags & 0x01) !== 0;
        		const frameData = arrayBuffer.slice(7);

        		// 初 H.264 フレーム受信 — デコーダ準備可否に関わらず配信は稼働しているためバッジを解除する
        		ProtocolManager.clearWcsInitBadge();

        		// キーフレーム受信時、デコーダ未初期化であれば即時初期化
        		if (isKeyFrame && !ProtocolManager._videoDecoderInitialized) {
        			// [task23 F-1] 保持中の SPS/PPS（description）を渡与
        			ProtocolManager.initVideoDecoder(ProtocolManager._videoDecoderDescription);
        		}

        		// デコーダ未初期化時のヌル参照ガード
        		if (!ProtocolManager._videoDecoderInitialized || !ProtocolManager._videoDecoder) {
        			return;
        		}

        		await ProtocolManager.decodeWebCodecsFrame(frameData, isKeyFrame, seq);
        		return;
        	} else if (type === 0x00) {
        		// 【Critical修正 指摘3】 Version 2 における WebP フレーム処理（7Bヘッダー）
        		// 7バイトヘッダーを正しくパースし、slice(7) でデータ部分を抽出
        		const frameData = arrayBuffer.slice(7);
       
        		// シーケンス番号の更新（受信直後）
        		const diff2 = (seq - latestSequenceNumber) | 0;
        		if (diff2 <= 0 && latestSequenceNumber !== 0) {
        			return;
        		}
        		latestSequenceNumber = seq;
        		// 内容変化 FPS 計測: 直前フレームとのシグネチャ比較（重複＝内容非変化の配信）
        		ProtocolManager._trackWebpContentChange(frameData);
       
        		// WebP 描画処理
        		if (typeof handleWebpFrame === 'function') {
        			handleWebpFrame(frameData, seq);
        		} else {
        			// handleWebpFrame が未定義時は fallback として createImageBitmap 直接使用
        			var blob = new Blob([frameData]);
        			var bitmap = await ProtocolManager.safeCreateImageBitmap(blob);
        			if (bitmap) {
        				drawBitmap(bitmap);
        				bitmap.close();
        			}
        		}
        		return;
        	} else {
        		console.warn('[ProtocolManager] Unknown frame type:', type);
        		if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] Unknown frame type: ' + type, 'UNKNOWN_FRAME');
        		return;
        	}
        }
       
        // Version 1: 6B ヘッダー（既存 WebP モード）
        const flagsV1 = view.getUint8(1);
        const seqV1 = view.getInt32(2, false);
        const frameDataV1 = arrayBuffer.slice(6);

        if (version > 1) {
            console.warn('[ProtocolManager] Unknown protocol version:', version, '(Expected: 0 or 1)');
            if (window.__eventLog) window.__eventLog.consoleLog('warn', `[ProtocolManager] Unknown protocol version: ${version} (Expected: 0 or 1)`, 'UNKNOWN_VERSION');
            return;
        }

        // [二次レビュー修正] シーケンス番号判定のタイミング修正
        // latestSequenceNumber を**受信直後**に更新し、Promiseチェーン内での判定が
        // 常に最新のシーケンス番号に基づいていることを保証する。
        // (seqV1 - latestSequenceNumber) | 0 は意図的に整数強制（オーバーフロー保護）
        const diff = (seqV1 - latestSequenceNumber) | 0;
        if (diff <= 0 && latestSequenceNumber !== 0) {
            return;
        }
        // 受信直後に latestSequenceNumber を更新（古いフレーム破棄の保証）
        latestSequenceNumber = seqV1;
        // 内容変化 FPS 計測: 直前フレームとのシグネチャ比較（重複＝内容非変化の配信）
        ProtocolManager._trackWebpContentChange(frameDataV1);

        // [🔍 plan02 diag] WebPデータ整合性検証（サンプリング: 10フレームに1回）
        if (ProtocolManager._validationEnabled) {
            ProtocolManager._validationCounter++;
        }
        const shouldValidate = ProtocolManager._validationEnabled && ProtocolManager._validationCounter % 10 === 0;

        if (shouldValidate && !validateWebpData(frameDataV1)) {
            console.warn('[ProtocolManager] Invalid WebP data (validation failed, sampled)');
            if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] Invalid WebP data (validation failed, sampled)', 'INVALID_WEBP');
            ProtocolManager._decodeErrorCount++;
            // [カウンター整合性] 検証失敗時は有効化を再開 + カウンターをリセット
            ProtocolManager._validationEnabled = true;
            ProtocolManager._validationCounter = 0;
            if (!ProtocolManager._initProtectionActive) {
                ProtocolManager._consecutiveDecodeErrors++;
                if (ProtocolManager._consecutiveDecodeErrors >= 3) {
                    const now2 = Date.now();
                    if (now2 - ProtocolManager._lastInitRequestTime >= ProtocolManager._minInitInterval) {
                        ProtocolManager._initRequestCount++;
                        console.log(`[ProtocolManager] 3回連続WebP検証失敗 → init要求を送信します (seq=${seqV1}, init要求回数: ${ProtocolManager._initRequestCount})`);
                        if (window.__eventLog) window.__eventLog.consoleLog('log', `[ProtocolManager] 3回連続WebP検証失敗 → init要求を送信します (seq=${seqV1}, init要求回数: ${ProtocolManager._initRequestCount})`, 'WEBP_INIT_REQ');
                        this.sendWsControl({ action: 'init' });
                        ProtocolManager._lastInitRequestTime = now2;
                        ProtocolManager._initProtectionActive = true;
                        setTimeout(() => {
                            ProtocolManager._initProtectionActive = false;
                        }, ProtocolManager._initProtectionMs);
                    }
                    ProtocolManager._consecutiveDecodeErrors = 0;
                }
            }
            return;
        }

        // [🚨 Critical 修正] デコードキュー飽和防止 - フレーム破棄ロジック
        // キュー長が閾値を超えた場合は新しいフレームを即座に破棄
        if (ProtocolManager._pendingDecodeCount >= ProtocolManager._maxDecodeQueue) {
            // 破棄（静かにドロップ - 正常なフロー制御）
            return;
        }

        // [🔍 plan02 根本原因対策] Promiseチェーンによる厳格なシリアライズデコード
        // 前のデコードが完了するまで次のデコードを開始しない
        // [二次レビュー修正] decodeAndDraw は安定したメソッド参照を使用（クロージャ生成抑制）
        ProtocolManager._pendingDecodeCount++;
        ProtocolManager._bitmapDecodeChain = ProtocolManager._bitmapDecodeChain
            .then(() => ProtocolManager.decodeAndDraw(frameDataV1, seqV1))
            .catch(e => {
                console.warn('[ProtocolManager] createImageBitmap decode error:', e.message);
                if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] createImageBitmap decode error: ' + e.message, 'DECODE_ERR');
                ProtocolManager._decodeErrorCount++;
                // [カウンター整合性] デコードエラー時は検証を有効化 + カウンターリセット
                ProtocolManager._validationEnabled = true;
                ProtocolManager._validationCounter = 0;
                if (!ProtocolManager._initProtectionActive) {
                    ProtocolManager._consecutiveDecodeErrors++;
                    if (ProtocolManager._consecutiveDecodeErrors >= 3) {
                        const now2 = Date.now();
                        if (now2 - ProtocolManager._lastInitRequestTime >= ProtocolManager._minInitInterval) {
                            ProtocolManager._initRequestCount++;
                            console.log(`[ProtocolManager] 3回連続デコードエラー → init要求を送信します (seq=${seqV1}, init要求回数: ${ProtocolManager._initRequestCount})`);
                            if (window.__eventLog) window.__eventLog.consoleLog('log', `[ProtocolManager] 3回連続デコードエラー → init要求を送信します (seq=${seqV1}, init要求回数: ${ProtocolManager._initRequestCount})`, 'DECODE_INIT_REQ');
                            this.sendWsControl({ action: 'init' });
                            ProtocolManager._lastInitRequestTime = now2;
                            ProtocolManager._initProtectionActive = true;
                            setTimeout(() => {
                                ProtocolManager._initProtectionActive = false;
                            }, ProtocolManager._initProtectionMs);
                        }
                        ProtocolManager._consecutiveDecodeErrors = 0;
                    }
                }
            });
    },

    // [🚨 Critical 修正] decodeAndDraw を ProtocolManager のメソッドとして定義
    // 呼び出しのたびに匿名関数として生成されないよう、安定した参照を提供する。
    // GC負荷低減 + メモリ断片化防止。
    async decodeAndDraw(frameData, seq) {
        try {
            // [🔍 plan03 1-0a] initフレーム vs 通常フレームの切り分けログ
            // seqと_lastInitSeqの差を計算してsinceLastInitを算出
            const sinceLastInit = seq - ProtocolManager._lastInitSeq;
            const isInitFrame = sinceLastInit <= 1;

            // [🔍 plan03 1-0a] WebPマジックナンバー（RIFF/WEBP）の検証
            const riffOk = frameData.length >= 12 &&
                frameData[0] === 0x52 && frameData[1] === 0x49 && // 'RI'
                frameData[2] === 0x46 && frameData[3] === 0x46 && // 'FF'
                frameData[8] === 0x57 && frameData[9] === 0x45 && // 'WE'
                frameData[10] === 0x42 && frameData[11] === 0x50; // 'BP'

            // [🔍 plan02 修正] WebPフォーマット統一 - サーバーはWebPを送信するため image/webp を使用
            const blob = new Blob([frameData], { type: 'image/webp' });
            const bitmap = await ProtocolManager.safeCreateImageBitmap(blob);

            if (!bitmap) {
                ProtocolManager._decodeErrorCount++;
                // [🔍 plan03 1-0a] デコード失敗時の詳細ログ（100フレームに1回に抑制）
                if (++ProtocolManager._plan03FailLogCounter % 100 === 0) {
                    console.warn(`[plan03-1-0a] DECODE_FAIL (sampled): seq=${seq}, sinceLastInit=${sinceLastInit}, isInit=${isInitFrame}, blobSize=${frameData.length}, RIFF=${riffOk}`);
                    if (window.__eventLog) window.__eventLog.consoleLog('warn', `[plan03-1-0a] DECODE_FAIL (sampled): seq=${seq}, sinceLastInit=${sinceLastInit}, isInit=${isInitFrame}, blobSize=${frameData.length}, RIFF=${riffOk}`, 'DECODE_FAIL_SAMPLED');
                }
                return;
            }

            // [🔍 plan03 1-0a] initフレーム受信時に _lastInitSeq を更新
            if (isInitFrame) {
                ProtocolManager._lastInitSeq = seq;
            }

            try {
                // [💡 Minor 修正] gameCtx の null チェック追加
                if (!gameCtx) {
                    console.error('[ProtocolManager] gameCtx is null - draw skipped');
                    if (window.__eventLog) window.__eventLog.consoleLog('error', '[ProtocolManager] gameCtx is null - draw skipped', 'DRAW_CTX_NULL');
                    return;
                }
                // [🔍 plan02 修正] screen オブジェクトへのアクセス前に null チェック
                if (!screen) {
                    console.error('[ProtocolManager] screen is null - draw skipped');
                    if (window.__eventLog) window.__eventLog.consoleLog('error', '[ProtocolManager] screen is null - draw skipped', 'DRAW_CTX_NULL');
                    return;
                }
                gameCtx.clearRect(0, 0, screen.width, screen.height);
                gameCtx.drawImage(bitmap, 0, 0);
            } finally {
                // [🔍 plan02 根本原因対策] ImageBitmapリソースの確実な解放
                if (bitmap && typeof bitmap.close === 'function') {
                    bitmap.close();
                }
            }

            // [🔍 plan02 1-5] デコード成功カウント
            ProtocolManager._decodeSuccessCount++;

            // [連続成功カウント] 指数バックオフリセット用
            ProtocolManager._consecutiveSuccess++;
            if (ProtocolManager._consecutiveSuccess >= 30) {
                // 30フレーム連続成功 → バックオフをリセット
                ProtocolManager._currentBackoffMs = ProtocolManager._initBackoffMs;
                ProtocolManager._consecutiveSuccess = 0;
                // [検証スキップ] 定常時は検証をスキップ（CPU負荷抑制）
                ProtocolManager._validationEnabled = false;
                ProtocolManager._validationCounter = 0;
            }

            ProtocolManager._consecutiveDecodeErrors = 0;
            ProtocolManager._initProtectionActive = false;

            // [二次レビュー修正] latestSequenceNumber は受信直後に更新されるため、
            // ここでの更新は不要（削除済み）

            drawMagnifier();

            status.textContent = 'SYNCHRONIZED';
            status.classList.add('ok');

            frameCount++;
            updateFpsDisplay();

        } finally {
            // [🚨 Critical 修正] キュー長カウントをデコード完了時にデクリメント
            ProtocolManager._pendingDecodeCount--;
        }
    },

    //================================================================================
    // ホスト側描画凍結のクライアント側検知（FROZEN バッジ / display_state banner）
    //================================================================================

    // FROZEN 判定ウォッチドッグ（2 秒間隔）。WS 接続中かつ 10 秒以上バイナリフレーム無しで
    // ハートビートも止まっている場合に「ホスト側描画凍結」の疑いとしてバッジを出す。
    // ハートビートは 15 秒周期のため、判定窓は 2 周期 + マージン（35 秒）より小さくしないこと
    // （周期未満の窓だと常時誤検知になる）。
    ensureFrozenWatchdog() {
        if (this._frozenWatchdogTimer) return;
        this._frozenWatchdogTimer = setInterval(() => this._frozenWatchdogTick(), 2000);
    },

    _frozenWatchdogTick() {
        const ws = this.wsConnection;
        if (!ws || ws.readyState !== WebSocket.OPEN) {
            // 切断中は接続状態表示が別系統（onWsClose）で更新されるためフラグのみ戻す
            this._frozenSuspected = false;
            return;
        }
        // 初フレーム受信前は判定しない（init 待ちは既存の初期化中ゲートが扱う）
        if (!this._lastBinaryFrameAt) return;

        const now = Date.now();
        if (now - this._lastBinaryFrameAt < 10000) {
            this._clearFrozenBadge();
            return;
        }

        // 10 秒以上フレーム無し: ハートビート生存で切り分け
        //   生存 → ホストは生きている（画面静止時はフレームを送らない仕様）→ 情報ヒントのみ
        //   停止 → ハートビートを送るループ自体が止まった = ホスト側描画/プロセス停止の疑い
        const noHeartbeat = !this._lastHeartbeatAt || (now - this._lastHeartbeatAt >= 35000);
        if (noHeartbeat) {
            this._showFrozenBadge();
        } else {
            this._clearFrozenBadge();
            if (!this._idleHinted) {
                this._idleHinted = true;
                showTempToast('ホスト側が待機中です（画面が静止しているため配信がありません）', 4000);
            }
        }
    },

    _showFrozenBadge() {
        if (this._frozenSuspected) return;
        this._frozenSuspected = true;
        this.updateConnStatus('FROZEN', false);
        if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] 10秒以上フレーム無しかつハートビート停止 → ホスト側描画凍結の疑い', 'FROZEN_SUSPECTED');
    },

    _clearFrozenBadge() {
        if (!this._frozenSuspected) return;
        this._frozenSuspected = false;
        this._idleHinted = false;
        this.updateConnStatus('WS', true);
    },

    // display_state 制御メッセージの banner。reason ごとの対策文言を添えて常時表示する
    _showDisplayBanner(reason) {
        const reasonText = {
            rdp_disconnected: 'RDP セッションが切断されています。再接続するか、コンソール側でダミープラグ/仮想ディスプレイを有効にしてください',
            no_monitor: 'モニタが検出されません。ダミープラグ（EDID エミュレータ）の挿入か仮想ディスプレイドライバを有効にしてください',
            session0: 'サービス（セッション 0）での実行は未対応です。デスクトップセッションで実行してください',
            render_stall: 'ホスト側の描画が停止しています。ワークステーションロックの無効化と電源プランのディスプレイオフ設定を確認してください',
            ok: ''
        };
        let el = document.getElementById('display-state-banner');
        if (!el) {
            el = document.createElement('div');
            el.id = 'display-state-banner';
            el.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:99999;padding:6px 12px;background:rgba(185,28,28,0.92);color:#fff;font:12px/1.5 monospace;text-align:center;pointer-events:none;display:none;';
            document.body.appendChild(el);
        }
        const detail = reasonText[reason] || reason || '';
        el.textContent = '⚠ ホスト側の表示が停止しています（画面が凍結する可能性があります）' + (detail ? '：' + detail : '');
        el.style.display = 'block';
    },

    _hideDisplayBanner() {
        const el = document.getElementById('display-state-banner');
        if (el) el.style.display = 'none';
    },

    updateConnStatus(text, isConnected) {
        const el = document.getElementById('conn-status');
        if (el) {
            el.textContent = text;
            if (isConnected) {
                el.classList.add('ok');
            } else {
                el.classList.remove('ok');
            }
        }
        // ミニ情報に直接反映（DOM逆引きなし）
        const hudMiniConn = document.getElementById('hud-mini-conn');
        if (hudMiniConn) {
            hudMiniConn.textContent = text; // 接続状態テキスト（conn-statusと一致）
            if (isConnected) {
                hudMiniConn.classList.add('ok');
            } else {
                hudMiniConn.classList.remove('ok');
            }
        }
    },

    // 「初期化中」バッジの解除: 初 H.264 関連フレーム（0x01/0x02）受信で通常表示へ戻す
    clearWcsInitBadge() {
        if (this._wcsWaitingFirstFrame) {
            this._wcsWaitingFirstFrame = false;
            this.updateConnStatus('WS', true);
        }
    },

    updateUIProtocolButtons() {
        const buttons = document.querySelectorAll('#protocol-group .settings-btn');
        buttons.forEach(btn => {
            const mode = btn.getAttribute('data-protocol');
            if (mode === this.currentMode) {
                btn.classList.add('active');
            } else {
                btn.classList.remove('active');
            }
        });
    },

    setMode(mode) {
        if (!this.MODES[mode.toUpperCase()]) return;
        this.currentMode = mode;
        localStorage.setItem('protocolMode', mode);
        this.updateUIProtocolButtons();
        this.startConnection();
    },

    getEffectiveMode() {
        return this.effectiveMode;
    },

    //================================================================================
    // VideoDecoder 関連機能
    //--------------------------------------------------------------------------------
    // VideoDecoder の初期化・デコード管理を行う。
    // ビットストリームは Annex B（start code 前置）運用のため description を configure に
    // 渡さない。保持中の description（AVCC 形式）は codec 文字列導出と、キーフレームへの
    // Annex B パラメータセット再構成（前置）のみに使う
    //================================================================================
    _videoDecoder: null,
    _videoDecoderInitialized: false,
    _videoDecoderDescription: null,  // SPS/PPS (AVCC 形式)・codec 導出と Annex B 前置の再構成専用
    _videoDecoderErrorCount: 0,
    _videoDecoderMaxErrors: 3,
    _videoDecoderReinitRequestTime: 0,

    // configure() に渡した codec 文字列の実値（採用値）。error コールバックは非同期発火のため
    // initVideoDecoder の config ローカル変数のスコープ外となり、採用値を保持しなければ
    // VD_ERROR 1 行からデコーダ構成を復元できない
    _videoDecoderAdoptedCodec: null,

    // 当該 WCS 試行の初回フレーム描画済みフラグ: エラーカウンタのリセットは初回成功時（output 内）と
    // 明示的モード切替でのみ行う。リトライのたびにリセットすると閾値（3 回）に永久に到達せず、
    // WebP フォールバックへ遷移できない（無限リトライループ）ため
    _videoDecoderFirstFrameRendered: false,

    // WebP 退避バッジ表示フラグ: フォールバック発動でセット、onopen で再提示、switchToWcsMode で解除
    _wcsFallbackBadge: false,

    // キーフレーム待ちガード: 再初期化（description 変更・エラーリトライ）後は最初のチャンクが
    // キーフレームである必要があり（仕様上の [[key chunk required]]）、delta を decode() すると
    // DataError となる。delta を JS レイヤーでドロップして構造的に回避する（ドロップはエラー計数に含めない）
    _wcsWaitKeyFrame: false,
    _wcsWaitKeyDropCount: 0,
    _wcsWaitKeySince: 0,

    // Annex B パラメータセット前置の発生回数（低頻度ログ可視化用・キーフレーム毎にインクリメント）
    _wcsAnnexBPrefixCount: 0,

    // サーバー伝達 codec（capture-config.codec・未伝達時 null）。この値はエンコーダ設定値であり
    // 実ビットストリームの実測値ではない（エンコーダは設定と異なる profile/level を出力し得る）。
    // description 未受領時の代替に留め、実 SPS からの導出を優先する
    serverCodec: null,

    // VideoDecoder.configure() に渡す codec 文字列を解決する（優先順位: AVCC description 導出 → サーバー伝達値 → 固定フォールバック）。
    // AVCC（AVCDecoderConfigurationRecord）の byte1-3 は SPS byte1-3（profile_idc / constraint_set_flags / level_idc）と
    // 同値であり、codec 文字列サフィックスはこれら 3 バイトの 16 進表記（RFC 6381 §3.4）。サポート判定は codec 文字列が
    // 示す正確な profile/level/constraint を要求するため、実ストリームと一致する実 SPS 導出を最優先とする。
    // サーバー伝達値はエンコーダ設定値であり実測値ではないので導出不能時の代替に留める。
    // 導出の前提は最小長 4 バイトかつ configurationVersion(=1)。満たさない場合は導出不能（境界外アクセスは
    // codec 文字列を破綻させる）ため代替へ退避する
    resolveCodecString(desc) {
        if (desc && desc.byteLength >= 4) {
            const u8 = new Uint8Array(desc);
            if (u8[0] === 0x01) {
                const hex2 = n => ('0' + n.toString(16)).slice(-2);
                return 'avc1.' + hex2(u8[1]) + hex2(u8[2]) + hex2(u8[3]);
            }
        }
        if (this.serverCodec) return this.serverCodec;
        return 'avc1.420028';
    },

    // description（AVCC ArrayBuffer）のバイト列一致判定: 同一バイト列の連続受信で再初期化をスキップするため
    _descriptionsEqual(a, b) {
        if (!a || !b || a.byteLength !== b.byteLength) return false;
        const ua = new Uint8Array(a);
        const ub = new Uint8Array(b);
        for (let i = 0; i < ua.length; i++) {
            if (ua[i] !== ub[i]) return false;
        }
        return true;
    },

    // AVCC（AVCDecoderConfigurationRecord）から Annex B パラメータセット（00 00 00 01 + SPS + 00 00 00 01 + PPS）を再構成する。
    // レイアウト根拠は生成側 _buildAvcc（ISO/IEC 14496-15 §5.3.3.1）:
    // [0]=configurationVersion(1) / [1-3]=profile・constraint・level / [4]=0xFF / [5]=0xE1(reserved 111 + numSPS=1) /
    // [6-7]=SPS 長(BE16) / [8..]=SPS 本体 / [8+spsLen]=numPPS(1) / [9-10+spsLen]=PPS 長(BE16) / [11+spsLen..]=PPS 本体。
    // 変換不能時は null を返し、呼び出し側は「前置なしで元チャンクをそのまま decode()」へ安全フォールバックする
    // （＝in-band 依存に戻るだけで新規の害は生まない）。全読み取りは境界チェックの後に行い、
    // 境界外アクセス（暗黙 undefined）を発生させない
    _buildAnnexBParameterSets(desc) {
        if (!desc || desc.byteLength < 11) return null;              // ヘッダ 6B + SPS 長 2B + numPPS/PPS 長 3B の下限
        const u8 = (desc instanceof Uint8Array) ? desc : new Uint8Array(desc);
        if (u8[0] !== 0x01) return null;                             // configurationVersion（生成側は 1 固定）
        if (u8[5] !== 0xE1) return null;                             // reserved(3)=111 + numSPS=1（生成側は 1 固定）
        const spsLen = (u8[6] << 8) | u8[7];
        if (spsLen === 0 || 8 + spsLen + 3 > u8.length) return null; // numPPS / PPS 長の読み出し境界
        if (u8[8 + spsLen] !== 0x01) return null;                    // numPPS=1（生成側は 1 固定）
        const ppsLen = (u8[9 + spsLen] << 8) | u8[10 + spsLen];
        if (ppsLen === 0 || 11 + spsLen + ppsLen > u8.length) return null; // PPS 本体の読み出し境界
        const sps = u8.subarray(8, 8 + spsLen);
        const pps = u8.subarray(11 + spsLen, 11 + spsLen + ppsLen);
        const out = new Uint8Array(8 + spsLen + ppsLen);             // start code 4B ×2 + SPS + PPS
        out.set([0, 0, 0, 1], 0);
        out.set(sps, 4);
        out.set([0, 0, 0, 1], 4 + spsLen);
        out.set(pps, 8 + spsLen);
        return out;
    },

    // [task19 P4] Clamp 連続計測（レビュー Minor: 連続Clamp時はサーバー側フレーム生成に問題の可能性）
    // 連続 3 回 Clamp 発生でサーバーに reinit_decoder を要求（5秒スロットリング付き）
    _clampConsecutiveCount: 0,
    _clampTotalCount: 0,
    _clampReinitThreshold: 3,
    _clampReinitIntervalMs: 5000,
    
    // [task19 v3] WebCodecs 用の独立 timestamp カウンタ（Critical 5 修正）
    _videoDecoderTimestampCounter: 0,
    _videoDecoderLastTimestamp: 0,
    
    // [task19 v3] Critical 8 修正: TOCTOU 競合防止の排他制御
    _videoDecoderInitializing: false,

    // VideoDecoder の初期化（SPS/PPS が必要）
    initVideoDecoder(description) {
        // [task19 v3] Critical 8 修正: TOCTOU 競合防止の排他制御
        if (this._videoDecoderInitializing) {
            console.log('[ProtocolManager] initVideoDecoder already in progress, skipping');
            return false;
        }
        this._videoDecoderInitializing = true;

        if (!('VideoDecoder' in window)) {
            console.warn('[ProtocolManager] VideoDecoder not supported');
            if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] VideoDecoder not supported', 'VD_NOT_SUPPORTED');
            this._videoDecoderInitializing = false;
            return false;
        }

        try {
            // 旧デコーダの解放
            if (this._videoDecoder) {
                try { this._videoDecoder.close(); } catch (e) {
                    // [task19 第4回Minor] 例外の握りつぶし禁止（AGENTS.md NEVER）: 旧デコーダ解放時の close() 失敗をログに記録
                    console.warn('[ProtocolManager] VideoDecoder.close() failed (initVideoDecoder old decoder release path):', e && e.message ? e.message : e);
                    if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] VideoDecoder.close() failed (initVideoDecoder old decoder release path): ' + (e && e.message ? e.message : e), 'VD_CLOSE_FAIL');
                }
                this._videoDecoder = null;
            }

            // 引数なし呼び出し時の undefined・空 ArrayBuffer（byteLength === 0）による上書き破棄防止
            // （受信済み SPS/PPS が空値で消ると codec 導出と Annex B 前置の元データが失われる）
            if (description && description.byteLength > 0) {
                this._videoDecoderDescription = description;
            }

            this._videoDecoder = new VideoDecoder({
                output: (frame) => {
                    // [task23 F-0-2] 現行の output 内 frame.close() を削除し、close 所有権を drawVideoFrame に単一化
                    // （W3C "Close is final" に従い、drawImage 直後に drawVideoFrame 内で close() する）
                    if (typeof drawVideoFrame === 'function') {
                        // [task23 Minor-2] null frame 時は drawVideoFrame(frame) の関数呼出を呼び出し側でガード
                        // （drawVideoFrame 本体は `if (!frame) return;` で早期 return するため機能的には安全だが、
                        //  呼び出し側で null をガードし、null frame ごとに発生していた不要な関数呼出を排除する。
                        //  null frame 時は frame が存在しないため、close も副次処理も実行しない）
                        if (frame) {
                            drawVideoFrame(frame);
                            // 初回デコード成功時のみエラーカウンタをクリア: リトライのたびの自己リセットは
                            // 閾値到達を永久に妨げるため、初回成功・明示的モード切替・フォールバック確定時のみリセットする
                            if (!this._videoDecoderFirstFrameRendered) {
                                this._videoDecoderFirstFrameRendered = true;
                                this._videoDecoderErrorCount = 0;
                            }
                            // [task23 指摘5] 描画副次処理は output コールバックに残留（責務分離）
                            // [task23 指摘2] frame が null 時は副次処理をスキップ（frameCount 過剰インクリメント・FPS 表示ズレ防止）
                            frameCount++;
                            updateFpsDisplay();
                            drawMagnifier();
                            status.textContent = 'SYNCHRONIZED';
                            status.classList.add('ok');
                        }
                    } else {
                        // [task23 Minor-1] drawVideoFrame 未定義時の緊急フォールバック（実務到達不可）
                        // drawVideoFrame は remote_screen.js:395 で必ず window に公開されるため、
                        // 本分岐は通常到達しない（リーク防止の安全策）。
                        // 描画経路（drawVideoFrame）が存在しないため、副次処理（frameCount++ /
                        // updateFpsDisplay / drawMagnifier / status 更新）は実行せず、
                        // frame のリーク防止のため安全に close() して解放する。
                        console.warn('[ProtocolManager] drawVideoFrame not defined, closing frame');
                        if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] drawVideoFrame not defined, closing frame', 'DRAW_FN_MISSING');
                        if (frame && typeof frame.close === 'function') frame.close();
                    }
                },
                error: (e) => {
                    console.error('[ProtocolManager] VideoDecoder error:', e);
                    // VD_ERROR に採用構成を付与: メッセージ文だけでは構成を復元できない。
                    // waitKey=false は初期構成の形式不整合、true は再初期化後の過渡エラーであり、
                    // 原因もとるべき対策も別物（ログ 1 行で切り分けられるようにする）
                    const errDescLen = (this._videoDecoderDescription && this._videoDecoderDescription.byteLength) || 0;
                    if (window.__eventLog) window.__eventLog.consoleLog('error', `[ProtocolManager] VideoDecoder error: ${e && e.message ? e.message : e} (codec=${this._videoDecoderAdoptedCodec || 'unknown'} desc=${errDescLen} fmt=annexb waitKey=${this._wcsWaitKeyFrame})`, 'VD_ERROR');
                    this._videoDecoderErrorCount++;
                    // [task19 v5] 最大3回までリトライ
                    if (this._videoDecoderErrorCount <= this._videoDecoderMaxErrors) {
                        console.warn(`[ProtocolManager] VideoDecoder error retry ${this._videoDecoderErrorCount}/${this._videoDecoderMaxErrors}`);
                        if (window.__eventLog) window.__eventLog.consoleLog('warn', `[ProtocolManager] VideoDecoder error retry ${this._videoDecoderErrorCount}/${this._videoDecoderMaxErrors}`, 'VD_ERROR_RETRY');
                        if (this._videoDecoderDescription) {
                            // デコードエラーで UA がデコーダを自動クローズする（Close is final）。
                            // クローズ済みへの close() は InvalidStateError のため、closed のときはスキップする
                            if (this._videoDecoder && this._videoDecoder.state !== 'closed') {
                                try { this._videoDecoder.close(); } catch (e2) {
                                    // close() 失敗はログ記録（AGENTS.md NEVER 違反の例外握りつぶし防止）
                                    console.warn('[ProtocolManager] VideoDecoder.close() failed (error retry path):', e2 && e2.message ? e2.message : e2);
                                    if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] VideoDecoder.close() failed (error retry path): ' + (e2 && e2.message ? e2.message : e2), 'VD_CLOSE_FAIL');
                                }
                            }
                            this._videoDecoder = null;
                            this._videoDecoderInitialized = false;
                            // [task19 レビュー指摘 M5] 二重フラグ解放の解消:
                            // 本 error コールバックは非同期に発火するため、発火時点では直前の
                            // initVideoDecoder の finally が既にフラグを解放済み。ここで手動解放すると、
                            // 別経路（Type=0x02 受信）の初期化が進行中（フラグ=true）のタイミングで
                            // フラグを強制 false にしてしまい、進行中初期化と並走する二重デコーダ生成を
                            // 引き起こす競合が発生する。手動解放を削除し finally の解放のみに一本化:
                            // - 進行中初期化なし: フラグは false のままなので再帰呼び出しは正常実行（リトライ維持）
                            // - 進行中初期化あり: フラグは true なので再帰呼び出しは TOCTOU チェックで
                            //   スキップされる（進行中初期化が新デコーダ生成を行うため、リトライは冗長で正しい挙動）
                            // 再初期化後は最初のチャンクがキーフレーム必須となるため、待ちガードを有効化する
                            this._wcsWaitKeyFrame = true;
                            this._wcsWaitKeyDropCount = 0;
                            this._wcsWaitKeySince = Date.now();
                            this.initVideoDecoder(this._videoDecoderDescription);
                        }
                    } else {
                        console.error('[ProtocolManager] VideoDecoder max errors exceeded, falling back to WebP');
                        if (window.__eventLog) window.__eventLog.consoleLog('error', '[ProtocolManager] VideoDecoder max errors exceeded, falling back to WebP', 'VD_FALLBACK_WEBP');
                        this._videoDecoder = null;
                        this._videoDecoderInitialized = false;
                        // フォールバックは当該 WCS 試行の終端確定: ここでのゼロクリアは次試行（再接続/setMode）を
                        // 1 エラー目で即再フォールバックさせないためのもの（リトライ中のリセットとは役割が異なる）
                        this._videoDecoderErrorCount = 0;
                        // 退避の可視化: setMode の再接続で onopen がバッジを 'WS' に上書きするため、
                        // フラグ経由で退避状態を再提示する（解除は switchToWcsMode のみ）
                        this._wcsFallbackBadge = true;
                        this.updateConnStatus('低遅延モード不可 — WebP に退避中', true);
                        if (typeof ProtocolManager.setMode === 'function') {
                            ProtocolManager.setMode('websocket');
                        }
                    }
                }
            });

            const config = {
                // description(AVCC) 導出 → サーバー伝達値 → 固定値の順で解決（codec 文字列は実ストリームの
                // profile/level と一致必須）。codedWidth/Height はデコーダ実装選択のヒントであり、
                // description 廃止後は実寸との整合が意味を持つ（エンコーダ固定値 1200x720 を維持）
                codec: this.resolveCodecString(this._videoDecoderDescription),
                codedWidth: 1200,
                codedHeight: 720,
                optimizeForLatency: true
            };
            // description を渡与しない: 仕様上 description 在りなら bitstream は avc 形式（NAL 長前置り）と
            // 解釈されるが、実配信は Annex B（start code 前置）であり組合せるとチャンク先頭から解析不能となる。
            // description 無しは annexb 形式と解釈されるため、Annex B 運用では渡さないのが正しい

            this._videoDecoder.configure(config);

            this._videoDecoderInitialized = true;
            // 採用 codec と description 長を記録: 実 SPS Level との突合（黒画面切り分け）の証跡
            const descLen = (this._videoDecoderDescription && this._videoDecoderDescription.byteLength) || 0;
            this._videoDecoderAdoptedCodec = config.codec;
            console.log(`[ProtocolManager] VideoDecoder 初期化成功 (codec=${config.codec} descLen=${descLen})`);
            if (window.__eventLog) window.__eventLog.consoleLog('log', `[ProtocolManager] VideoDecoder 初期化成功 codec=${config.codec} descLen=${descLen}`, 'VD_INIT_OK');
            return true;
        } catch (e) {
            // [task19 v3] Major 4 修正: catch した例外もエラーカウントに反映
            this._videoDecoderErrorCount++;
            console.error(`[ProtocolManager] initVideoDecoder exception (count: ${this._videoDecoderErrorCount}):`, e);
            if (window.__eventLog) window.__eventLog.consoleLog('error', `[ProtocolManager] initVideoDecoder exception (count: ${this._videoDecoderErrorCount}):` + (e && e.message ? ' ' + e.message : ' ' + e), 'VD_INIT_EX');

            // [task19 v4] Major 1 修正: デコーダ生成後の例外時にリソースを確実に解放
            if (this._videoDecoder) {
                // クローズ済みへの close() は InvalidStateError のため、closed のときはスキップする
                if (this._videoDecoder.state !== 'closed') {
                    try { this._videoDecoder.close(); } catch (e2) {
                        // close() 失敗はログ記録（AGENTS.md NEVER 違反の例外握りつぶし防止）
                        console.warn('[ProtocolManager] VideoDecoder.close() failed (initVideoDecoder exception path):', e2 && e2.message ? e2.message : e2);
                        if (window.__eventLog) window.__eventLog.consoleLog('warn', '[ProtocolManager] VideoDecoder.close() failed (initVideoDecoder exception path): ' + (e2 && e2.message ? e2.message : e2), 'VD_CLOSE_FAIL');
                    }
                }
                this._videoDecoder = null;
            }

            return false;
        } finally {
            // [task19 v3] Critical 8 修正: 排他フラグを解放
            this._videoDecoderInitializing = false;
        }
    },

    // WebCodecs バイナリフレームのデコード処理
    async decodeWebCodecsFrame(data, isKeyFrame, seq) {
        if (!this._videoDecoder || !this._videoDecoderInitialized) {
            return;
        }

        // キーフレーム待ちガード: 再初期化後は仕様上最初のチャンクがキーフレーム必須で、
        // delta を decode() すると DataError になる。delta は decode() 前にドロップする
        // （ドロップはエラー計数に含めない — ガードがフォールバック到達性を損なうのは意図しない）
        if (this._wcsWaitKeyFrame) {
            if (!isKeyFrame) {
                this._wcsWaitKeyDropCount++;
                if (this._wcsWaitKeyDropCount === 1 || this._wcsWaitKeyDropCount % 10 === 0) {
                    console.warn(`[ProtocolManager] Waiting for key frame after decoder re-init, dropping delta (count=${this._wcsWaitKeyDropCount})`);
                    if (window.__eventLog) window.__eventLog.consoleLog('warn', `[ProtocolManager] Waiting for key frame after decoder re-init, dropping delta (count=${this._wcsWaitKeyDropCount})`, 'VD_WAIT_KEY_DROP');
                }
                // ガード出口: 閾値（30 枚超過 / 5 秒超過）までキーフレームが来ない場合はサーバー側の
                // エンコーダ再初期化を要求して黒画面スタールを防ぐ（既存の 5 秒スロット様式を流用し要求の連打を防止）
                const waitNow = Date.now();
                if ((this._wcsWaitKeyDropCount > 30 || waitNow - this._wcsWaitKeySince > 5000) &&
                    waitNow - this._videoDecoderReinitRequestTime >= this._clampReinitIntervalMs) {
                    this._videoDecoderReinitRequestTime = waitNow;
                    console.warn(`[ProtocolManager] Key-frame wait exceeded threshold (drops=${this._wcsWaitKeyDropCount}), requesting reinit_decoder`);
                    if (window.__eventLog) window.__eventLog.consoleLog('warn', `[ProtocolManager] Key-frame wait exceeded threshold (drops=${this._wcsWaitKeyDropCount}), requesting reinit_decoder`, 'VD_REINIT_REQ');
                    if (typeof sendControlAction === 'function') {
                        sendControlAction('reinit_decoder');
                    }
                }
                return;
            }
            // キーフレーム到達でガードを解除し、以降は通常デコードへ復帰
            this._wcsWaitKeyFrame = false;
            this._wcsWaitKeyDropCount = 0;
        }

        const fps = ProtocolManager.webCodecsFps || 30;

        // [task19 v3] Critical 2 修正: マイクロ秒単位 (μs)
        // Critical 5 修正: 独立カウンタで単調増加を保証
        this._videoDecoderTimestampCounter++;
        let timestamp = Math.floor(this._videoDecoderTimestampCounter * 1_000_000 / fps);

        // [task19 v5] Critical 1/5/7 修正: Clamp 方式による単調増加保証（永久ループ解消版）
        if (timestamp <= this._videoDecoderLastTimestamp) {
            // [task19 P4] Clamp 連続計測（レビュー Minor 対応: サーバー側のフレーム生成ジッタ可観測性）
            this._clampTotalCount++;
            this._clampConsecutiveCount++;
            if (this._clampConsecutiveCount === 1 || this._clampConsecutiveCount % 10 === 0) {
                console.warn(`[ProtocolManager] Timestamp not monotonically increasing, clamping (consecutive=${this._clampConsecutiveCount}, total=${this._clampTotalCount})`);
                if (window.__eventLog) window.__eventLog.consoleLog('warn', `[ProtocolManager] Timestamp not monotonically increasing, clamping (consecutive=${this._clampConsecutiveCount}, total=${this._clampTotalCount})`, 'TS_CLAMP');
            }

            // [task19 P4] 連続 3 回 Clamp 発生はサーバー側のフレーム生成に問題がある可能性
            // （ジッタ / 遅延）のため、reinit_decoder を要求して SPS/PPS 再取得・デコーダ再構築を促す
            // 5秒スロットリングで要求の連打を防ぐ（既存 _videoDecoderReinitRequestTime を流用）
            if (this._clampConsecutiveCount >= this._clampReinitThreshold) {
                const now = Date.now();
                if (now - this._videoDecoderReinitRequestTime >= this._clampReinitIntervalMs) {
                    this._videoDecoderReinitRequestTime = now;
                    console.warn(`[ProtocolManager] Clamp consecutive count reached ${this._clampConsecutiveCount}, requesting reinit_decoder`);
                    if (window.__eventLog) window.__eventLog.consoleLog('warn', `[ProtocolManager] Clamp consecutive count reached ${this._clampConsecutiveCount}, requesting reinit_decoder`, 'VD_REINIT_REQ');
                    if (typeof sendControlAction === 'function') {
                        sendControlAction('reinit_decoder');
                    }
                }
            }

            const frameInterval = Math.floor(1_000_000 / fps);
            timestamp = this._videoDecoderLastTimestamp + frameInterval;
        } else {
            // 単調増加が保たれた場合は連続カウントをリセット
            this._clampConsecutiveCount = 0;
        }
        this._videoDecoderLastTimestamp = timestamp;

        try {
            // Annex B 運用では description を渡さないため、仕様上 key チャンクがデコードに必要な
            // 全パラメータセットを内包していることが期待される。エンコーダ挙動（IDR に SPS/PPS を
            // 付けるか）に依存せず保証するため、key チャンクには description から再構成した
            // パラメータセットを常に前置する（同一パラメータセットの再受領は Annex B では無害）。
            // 変換不能時は無前置で in-band 依存へ退避する
            if (isKeyFrame) {
                const ps = this._buildAnnexBParameterSets(this._videoDecoderDescription);
                if (ps) {
                    const merged = new Uint8Array(ps.byteLength + data.byteLength);
                    merged.set(ps, 0);
                    merged.set(new Uint8Array(data), ps.byteLength);
                    data = merged;
                    this._wcsAnnexBPrefixCount++;
                    // 前置は定常動作なので全件ログは帯域過大。初回と 30 件毎のみ記録する
                    if (this._wcsAnnexBPrefixCount === 1 || this._wcsAnnexBPrefixCount % 30 === 0) {
                        console.log(`[ProtocolManager] Annex B SPS/PPS prefixed to key chunk (count=${this._wcsAnnexBPrefixCount}, psBytes=${ps.byteLength})`);
                        if (window.__eventLog) window.__eventLog.consoleLog('log', `[ProtocolManager] Annex B SPS/PPS prefixed to key chunk (count=${this._wcsAnnexBPrefixCount}, psBytes=${ps.byteLength})`, 'VD_PS_PREFIX');
                    }
                }
            }

            var chunk = new EncodedVideoChunk({
                type: isKeyFrame ? 'key' : 'delta',
                data: data,
                timestamp: timestamp,
                duration: Math.floor(1_000_000 / fps)  // [task19 v3] マイクロ秒単位
            });

            await this._videoDecoder.decode(chunk);
        } catch (e) {
            // [task19 v3] Major 4 修正: decode 例外をエラーカウントに反映
            this._videoDecoderErrorCount++;
            console.error(`[ProtocolManager] decode exception (count: ${this._videoDecoderErrorCount}):`, e);
            if (window.__eventLog) window.__eventLog.consoleLog('error', `[ProtocolManager] decode exception (count: ${this._videoDecoderErrorCount}):` + (e && e.message ? ' ' + e.message : ' ' + e), 'VD_DECODE_EX');

            // エラー上限到達時はリトライ
            if (this._videoDecoderErrorCount >= this._videoDecoderMaxErrors) {
                if (typeof sendControlAction === 'function') {
                    sendControlAction('reinit_decoder');
                }
            }
        }
    }
};

//============================================================================================
// [task19 v6] WCS モード切替関数
// 既存 WebSocket 接続を維持したまま VideoDecoder 状態リセットのみを行う。
// [Round 7 Major 修正] switch_mode webcodecs の WS 送信は onopen ハンドラに一本化（本関数には送信を置かない）。
//============================================================================================
function switchToWcsMode() {
    try {
        // 1. 既存の VideoDecoder を解放
        // 【Secondary-Major 2 修正】 close() は非同期で完了するため、
        //    閉鎖前にローカル参照を取得し、ondequeueerror を null にして
        //    コールバックによる null 参照を防ぐ
        if (ProtocolManager._videoDecoder) {
            const decoder = ProtocolManager._videoDecoder;
            ProtocolManager._videoDecoder = null;
            // close() 完了後のコールバックによる null 参照を防止
            decoder.ondequeueerror = null;
            try {
                decoder.close();
            } catch (e) {
                // [task19 P4] 例外の握りつぶし禁止（AGENTS.md NEVER）: close() 失敗をログに記録
                console.warn('[switchToWcsMode] decoder.close() failed:', e && e.message ? e.message : e);
                if (window.__eventLog) window.__eventLog.consoleLog('warn', '[switchToWcsMode] decoder.close() failed: ' + (e && e.message ? e.message : e), 'VD_CLOSE_FAIL');
            }
        }

        // 2. 初期化状態をリセット
        ProtocolManager._videoDecoderInitialized = false;
        ProtocolManager._videoDecoderInitializing = false;
        ProtocolManager._videoDecoderDescription = null;
        ProtocolManager._videoDecoderAdoptedCodec = null;
        ProtocolManager._videoDecoderErrorCount = 0;
        // 明示的な WCS 再試行: エラーカウンタの初回成功リセットをやり直せるよう初回フラグも戻し、退避バッジを解除する
        ProtocolManager._videoDecoderFirstFrameRendered = false;
        ProtocolManager._wcsFallbackBadge = false;
        // キーフレーム待ちガードの計測状態も新試行用に振り直す
        ProtocolManager._wcsWaitKeyFrame = false;
        ProtocolManager._wcsWaitKeyDropCount = 0;
        ProtocolManager._wcsAnnexBPrefixCount = 0;
        ProtocolManager._videoDecoderTimestampCounter = 0;
        ProtocolManager._videoDecoderLastTimestamp = 0;
        // [task19 P4] Clamp 連続計測もリセット（モード再構築で計測状態を新規に開始）
        ProtocolManager._clampConsecutiveCount = 0;
        ProtocolManager._clampTotalCount = 0;
        ProtocolManager._videoDecoderReinitRequestTime = 0;

        // 3. [Round 7 Major 修正] switch_mode webcodecs の WS 送信は onopen ハンドラに一本化。
        //    本関数は VideoDecoder 状態リセットのみを行う（ここでは WS 送信を行わない）。
        //    理由: setMode('webcodecs') → startConnection() → closeWebSocket() + attemptWebSocket() の直後は
        //    新 WS が CONNECTING 状態で、sendWsControl は readyState === WebSocket.OPEN のときのみ
        //    送信するため no-op になる。実際の送信は接続確立後の onopen ハンドラ
        //    (this.currentMode === this.MODES.WEBCODECS 判定付き) で行われる。
    } finally {
        // [task19 レビュー指摘 M6] try-finally による状態整合保護（設計書 §5.2 二次独自 Minor 1 修正）:
        // close() 後の状態リセット処理で例外が発生した場合も、_videoDecoderInitializing が
        // true のまま残る（デッドロック的な TOCTOU 競合）状態を防ぐ
        ProtocolManager._videoDecoderInitializing = false;
    }
}

//============================================================================================
// [案C-2] WebPデータ検証関数
// [FPS Fix] WebPマジックナンバーチェック (RIFF/WEBP)
//           実行頻度は10フレームに1回のサンプリング
//============================================================================================
function validateWebpData(data) {
    const buf = new Uint8Array(data);

    // WebP最小サイズ: RIFF(4バイト) + サイズ(4バイト) + WEBP(4バイト) = 12バイト
    // 12バイト未満のデータは有効なWebPではない
    if (buf.length < 12) return false;

    // WebP RIFF マジックチェック (52 49 46 46 = 'R' 'I' 'F' 'F')
    if (buf[0] !== 0x52 || buf[1] !== 0x49 || buf[2] !== 0x46 || buf[3] !== 0x46) {
        return false;
    }

    // WebP フォーマットチェック (57 45 42 50 = 'W' 'E' 'B' 'P')
    // RIFFヘッダー(4バイト) + ファイルサイズ(4バイト) の後の8バイト目から
    if (buf[8] !== 0x57 || buf[9] !== 0x45 || buf[10] !== 0x42 || buf[11] !== 0x50) {
        return false;
    }

    return true;
}

//============================================================================================
// レスポンスヘッダーのバイトサイズを動的に計算するヘルパー関数
//============================================================================================
function getResponseHeaderBytes(response) {
    if (!USE_DYNAMIC_HEADER_CALC || !response || !response.headers) {
        return HTTP_HEADER_OVERHEAD_FIXED;
    }
    let size = 17;
    response.headers.forEach((val, key) => {
        size += key.length + val.length + 4;
    });
    const requestHeaderEst = 350 + (location.hostname ? location.hostname.length : 10);
    return size + requestHeaderEst;
}

//============================================================================================
// 受信バイト数を人間が読みやすい最適な単位にフォーマット
//============================================================================================
function updateDataUsage(bytes) {
    accumulatedBytes += bytes;
    let displayText;
    if (accumulatedBytes < 1024) {
        displayText = `${accumulatedBytes} B`;
    } else if (accumulatedBytes < 1024 * 1024) {
        displayText = `${(accumulatedBytes / 1024).toFixed(1)} KB`;
    } else if (accumulatedBytes < 1024 * 1024 * 1024) {
        displayText = `${(accumulatedBytes / (1024 * 1024)).toFixed(1)} MB`;
    } else {
        displayText = `${(accumulatedBytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
    }
    if (dataUsage) dataUsage.textContent = displayText;

    // ミニ情報に直接反映（system-monitor-barと完全一致）
    const hudMiniBw = document.getElementById('hud-mini-bw');
    if (hudMiniBw) {
        if (hudMiniBw.textContent !== displayText) {
            hudMiniBw.textContent = displayText; // e.g. "83.4 MB"
        }
    }
}

//============================================================================================
// 動的待機時間算出関数
//============================================================================================
function getDynamicDelay() {
    let delay;
    const isMaxFps = (dataFps === "60");
    if (!autoFpsEnabled || isMaxFps) {
        delay = calculateDelayByFps(targetFps);
    } else {
        const elapsed = Date.now() - lastOperationTime;
        if (elapsed < ACTIVE_TIMEOUT) {
            delay = calculateDelayByFps(targetFps);
        } else if (elapsed < IDLE_TIMEOUT) {
            delay = calculateDelayByFps(IDLE_FPS);
        } else {
            delay = calculateDelayByFps(SLEEP_FPS);
        }
    }

    if (delay !== lastSyncedDelay && typeof ProtocolManager !== 'undefined' && ProtocolManager.syncFps) {
        lastSyncedDelay = delay;
        // auto=true で送信: 自動省電力調整がサーバー側のエンコーダ FPS（手動設定優先）を上書きしないため
        ProtocolManager.syncFps(delay, false, true);
    }

    return delay;
}

//============================================================================================
// FPSからディレイへの変換共通関数
//============================================================================================
function calculateDelayByFps(fps) {
    if (fps >= 60 || fps <= 0) return FPS60_INTERVAL_MS; // 60FPSのインターバル（16ms）を返す
    return Math.round(1000 / fps);
}

//============================================================================================
// 移動制御通信の最適化
//============================================================================================
function sendMoveControl(x, y) {
    if (window.__eventLog) window.__eventLog.moveThrottled('move', x, y);
    if (ProtocolManager.effectiveMode === 'ws') {
        ProtocolManager.sendWsControl({ action: 'move', x, y });
        return;
    }

    if (isMoveSending) {
        pendingMoveCoords = { x, y };
        return;
    }
    isMoveSending = true;
    const moveUrl = `/control?action=move&x=${x}&y=${y}`;
    const reqHeaderOverhead = USE_DYNAMIC_HEADER_CALC ? 350 : HTTP_HEADER_OVERHEAD_FIXED;
    if (typeof updateDataUsage === 'function') {
        updateDataUsage(moveUrl.length + reqHeaderOverhead);
    }
    fetch(moveUrl)
        .catch(() => {})
        .finally(() => {
            isMoveSending = false;
            if (pendingMoveCoords) {
                const next = pendingMoveCoords;
                pendingMoveCoords = null;
                sendMoveControl(next.x, next.y);
            }
        });
}

//============================================================================================
// 制御コマンド送信
//============================================================================================
function sendControlAction(action, x, y) {
    if (window.__eventLog) window.__eventLog.action(action, x, y);
    if (ProtocolManager.effectiveMode === 'ws') {
        const data = { action };
        if (x !== undefined) data.x = x;
        if (y !== undefined) data.y = y;
        ProtocolManager.sendWsControl(data);
    } else {
        let url = `/control?action=${action}`;
        if (x !== undefined) url += `&x=${x}`;
        if (y !== undefined) url += `&y=${y}`;
        const reqHeaderOverhead = USE_DYNAMIC_HEADER_CALC ? 350 : HTTP_HEADER_OVERHEAD_FIXED;
        if (typeof updateDataUsage === 'function') {
            updateDataUsage(url.length + reqHeaderOverhead);
        }
        fetch(url).catch(() => {});
    }
}

//=============================================================================================
// [task06] スリープ復帰・リロード時の画面即時更新トリガー
//=============================================================================================
/**
 * [task06] スリープ復帰・リロード時の画面即時更新トリガー
 * - 二重ループの発生を防止しながら、即座に1回画面リフレッシュを実行する
 * - lastOperationTime には触れず、Auto-FPS 状態を破壊しない
 * - 100ms以内の重複呼出し（visibilitychangeとfocusの同時発生等）をタイムスタンプで確実にガード (KISS原則)
 */
function triggerResumeUpdate() {
    const now = Date.now();
    // [v5] 100ms以内の重複呼出しをタイムスタンプ判定で無視するガード
    if (now - (triggerResumeUpdate._lastTriggerTime || 0) < 100) {
        return;
    }
    triggerResumeUpdate._lastTriggerTime = now;

    // 初回のみログ出力（冗長化防止）
    if (!triggerResumeUpdate._logged) {
        console.log('[Resume] Immediate screen update triggered');
        triggerResumeUpdate._logged = true;
    }

    // 1. 次回の描画でフルキーフレームを要求
    isFirstWebpRequest = true;

    // 2. WebSocketモード時はinit要求を送信してサーバーにキーフレームを強制させる
    if (typeof ProtocolManager !== 'undefined' && ProtocolManager.effectiveMode === 'ws') {
        ProtocolManager.sendWsControl({ action: 'init' });
    }

    // 3. すでに画面更新処理が実行中（fetch / draw 待機中）の場合
    if (isUpdating) {
        // 次回のタイマー設定時に即時実行(delay 0)させるためペンディングフラグを設定
        triggerResumeUpdate._pendingImmediate = true;
        return;
    }

    // 4. 既存の待機タイマーを解除し、即座に更新を実行
    if (updateTimer !== null) {
        clearTimeout(updateTimer);
        updateTimer = null;
    }
    updateTimer = setTimeout(() => {
        updateScreenDiff();
    }, 0);
}
