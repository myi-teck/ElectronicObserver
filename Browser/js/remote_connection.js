//============================================================================================
// remote_connection.js
// WebSocket / HTTP通信・自動再接続・ステータス管理
// 依存: remote_config.js (定数・グローバル変数)
// 2026/08/01 myi 修正: 単一メッセージ受信対応・指数バックオフ・HUD同期・サンプリング検証
//============================================================================================

//============================================================================================
// ProtocolManager: WebSocket / HTTP の自動切替・フォールバック・再接続を管理
//============================================================================================
const ProtocolManager = {
    MODES: { AUTO: 'auto', WEBSOCKET: 'websocket', HTTP: 'http' },
    currentMode: null,
    effectiveMode: null,
    wsConnection: null,
    wsReconnectAttempts: 0,
    maxReconnectAttempts: 5,
    reconnectDelayMs: 1000,
    wsHeartbeatTimer: null,
    wsHeartbeatIntervalMs: 15000,
    httpPollingActive: false,

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

    // [HUD同期] init_hud 受信フラグ
    _hudInitialized: false,
    _hudInitTimeout: null,

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
        this.closeWebSocket();
        this.httpPollingActive = false;

        if (this.currentMode === this.MODES.WEBSOCKET) {
            this.effectiveMode = 'ws';
            this.attemptWebSocket();
        } else if (this.currentMode === this.MODES.AUTO) {
            this.effectiveMode = 'ws';
            this.attemptWebSocket();
        } else {
            this.effectiveMode = 'http';
            this.httpPollingActive = true;
            this.updateConnStatus('HTTP', true);
        }
    },

    attemptWebSocket() {
        const portStr = location.port ? ':' + location.port : '';
        const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
        const wsUrl = `${protocol}//${location.hostname}${portStr}/ws`;
        console.log('[ProtocolManager] WebSocket 接続試行中:', wsUrl);
        try {
            this.wsConnection = new WebSocket(wsUrl);
        } catch (e) {
            console.warn('[ProtocolManager] WebSocket creation failed:', e.message);
            if (this.currentMode === this.MODES.WEBSOCKET) {
                this.reconnectWebSocket();
            } else if (this.currentMode === this.MODES.AUTO) {
                this.fallbackToHttp();
            }
            return;
        }

        this.wsConnection.binaryType = 'arraybuffer';

        this.wsConnection.onopen = () => {
            console.log('[ProtocolManager] WebSocket connected successfully:', wsUrl);
            this.wsReconnectAttempts = 0;
            this.effectiveMode = 'ws';
            this.updateConnStatus('WS', true);

            // [指数バックオフリセット] 接続成功時にバックオフをリセット
            this._currentBackoffMs = this._initBackoffMs;
            console.log(`[ProtocolManager] Backoff reset to ${this._initBackoffMs}ms`);

            // [HUD同期] 接続成功時にHUD未初期化フラグをリセット
            this._hudInitialized = false;
            if (this._hudInitTimeout) {
                clearTimeout(this._hudInitTimeout);
                this._hudInitTimeout = null;
            }

            if (typeof getDynamicDelay === 'function') {
                this.syncFps(getDynamicDelay(), true);
            }

            // [HUD同期] 5秒でinit_hudタイムアウト（強制初期化）
            this._hudInitTimeout = setTimeout(() => {
                if (!this._hudInitialized) {
                    console.error(`[ProtocolManager] init_hud timeout (5s): proceeding without HUD`);
                    this._hudInitialized = true; // フォース初期化
                }
            }, 5000);

            // [FPS Fix] init要求送信を100ms遅延させ、サーバー側の準備を待つ
            setTimeout(() => {
                console.log('[ProtocolManager] init キーフレーム要求を送信します');
                ProtocolManager._initRequestCount++;
                console.log(`[ProtocolManager] init 要求カウント: ${ProtocolManager._initRequestCount}`);
                this.sendWsControl({ action: 'init' });
            }, 100);

            this.startHeartbeat();
        };

        this.wsConnection.onmessage = (event) => {
            if (typeof event.data === 'string') {
                if (typeof updateDataUsage === 'function') {
                    updateDataUsage(event.data.length + WS_FRAME_OVERHEAD);
                }
                if (event.data === 'heartbeat') {
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
                this.handleScreenUpdateBinary(event.data);
            }
        };

        this.wsConnection.onerror = () => {
            console.warn('[ProtocolManager] WebSocket error');
        };

        this.wsConnection.onclose = () => {
            this.onWsClose();
        };
    },

    onWsClose() {
        console.warn('[ProtocolManager] WebSocket closed');
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

        if (this.currentMode === this.MODES.AUTO) {
            this.fallbackToHttp();
        } else if (this.currentMode === this.MODES.WEBSOCKET) {
            this.reconnectWebSocket();
        }
    },

    reconnectWebSocket() {
        if (this.wsReconnectAttempts >= this.maxReconnectAttempts) {
            console.warn('[ProtocolManager] Max reconnect attempts reached, falling back to HTTP');
            this.fallbackToHttp();
            return;
        }

        // [指数バックオフ] ジャITTER付きの再接続遅延
        const jitter = Math.floor(Math.random() * this._jitterRangeMs) - this._jitterRangeMs / 2;
        const delay = Math.max(0, this._currentBackoffMs + jitter);
        this.wsReconnectAttempts++;
        this.updateConnStatus(`WS RETRY (${this.wsReconnectAttempts})`, false);
        console.warn(`[ProtocolManager] 再接続を試みます (delay=${delay}ms, base=${this._currentBackoffMs}ms, jitter=${jitter}ms)`);

        // 次回以降のバックオフ間隔を算出（最大値で飽和）
        this._currentBackoffMs = Math.min(
            this._currentBackoffMs * this._backoffMultiplier,
            this._maxBackoffMs
        );

        setTimeout(() => this.attemptWebSocket(), delay);
    },

    fallbackToHttp() {
        console.log('[ProtocolManager] Falling back to HTTP Polling');
        this.closeWebSocket();
        this.effectiveMode = 'http';
        this.httpPollingActive = true;
        this.updateConnStatus('HTTP (FB)', true);
    },

    closeWebSocket() {
        if (this.wsConnection) {
            try { this.wsConnection.close(); } catch {}
            this.wsConnection = null;
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

    syncFps(intervalMs, force = false) {
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
            this.sendWsControl({ action: 'set_fps', intervalMs: intervalMs });
            console.log('[ProtocolManager] syncFps送信: intervalMs=' + intervalMs + ', force=' + force);
        }
    },

    processWsControlMessage(jsonStr) {
        try {
            const msg = JSON.parse(jsonStr);
            if (msg.type === 'set_quality') {
                console.log('[ProtocolManager] Server set quality:', msg.quality);
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
                if (msg.url) {
                    window.location.href = msg.url;
                }
            } else if (msg.type === 'init_hud') {
                // [HUD同期] 初期HUDデータを受信
                console.log('[ProtocolManager] Received init_hud data');
                this._hudInitialized = true;
                // タイムアウトをクリア
                if (this._hudInitTimeout) {
                    clearTimeout(this._hudInitTimeout);
                    this._hudInitTimeout = null;
                }
                if (msg.data && typeof updateHudData === 'function') {
                    updateHudData(msg.data);
                }
            }
        } catch (e) {
            console.warn('[ProtocolManager] Invalid control message:', e.message);
        }
    },

    // [二次レビュー修正] シーケンス番号判定のタイミング修正
    // _frameReceiveCount と latestSequenceNumber の更新を受信直後に実行し、
    // Promiseチェーン内での判定が常に最新のシーケンス番号に基づいていることを保証する。
    handleScreenUpdateBinary(arrayBuffer) {
        if (!arrayBuffer || arrayBuffer.byteLength <= 6) return;

        // [HUD同期] init_hud 未受信時は描画をスキップ（タイムアウト後には強制初期化されるため描画継続）
        if (!this._hudInitialized) {
            return;
        }

        // [FPS Fix] 可観測性: フレーム受信カウントを**受信直後**に更新
        // [二次レビュー修正] _frameReceiveCount の更新タイミングを受信直後に移動
        ProtocolManager._frameReceiveCount++;

        // [🔍 plan02 diag] WebSocket受信状態の可視化
        const frameTime = performance.now();
        const isBuffer = arrayBuffer instanceof ArrayBuffer;
        if (this._lastFrameReceiveTime > 0) {
            const jitter = frameTime - this._lastFrameReceiveTime;
            if (jitter > 50) {
                // 50msを超えるジッターは異常とみなしてログ出力
                console.log(`[plan02-diag] Frame jitter: ${jitter.toFixed(1)}ms (binaryType=${isBuffer ? 'arraybuffer' : 'other'}, seq=${ProtocolManager._frameReceiveCount})`);
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
            console.log(`[ProtocolManager] 受信FPS: ${fps.toFixed(1)} | デコード成功FPS: ${decodeSuccessFps.toFixed(1)} | デコード成功率: ${decodeSuccessRate.toFixed(1)}% (フレーム数: ${ProtocolManager._frameReceiveCount}, デコード成功: ${ProtocolManager._decodeSuccessCount}, デコードエラー: ${ProtocolManager._decodeErrorCount}, init要求: ${ProtocolManager._initRequestCount}, キュー長: ${ProtocolManager._pendingDecodeCount})`);
            ProtocolManager._frameReceiveCount = 0;
            ProtocolManager._decodeSuccessCount = 0;
            ProtocolManager._frameReceiveStartTime = now;
            ProtocolManager._decodeErrorCount = 0;
        }

        // [単一メッセージ受信] 先頭6バイトをヘッダー、以降をデータとして処理
        const view = new DataView(arrayBuffer);
        const version = view.getUint8(0);
        const flags = view.getUint8(1);
        const seq = view.getInt32(2, false);
        const frameData = arrayBuffer.slice(6);

        if (version > 0) {
            console.warn('[ProtocolManager] Unknown protocol version:', version, '(Expected: 0)');
            return;
        }

        // [二次レビュー修正] シーケンス番号判定のタイミング修正
        // latestSequenceNumber を**受信直後**に更新し、Promiseチェーン内での判定が
        // 常に最新のシーケンス番号に基づいていることを保証する。
        // (seq - latestSequenceNumber) | 0 は意図的に整数強制（オーバーフロー保護）
        const diff = (seq - latestSequenceNumber) | 0;
        if (diff <= 0 && latestSequenceNumber !== 0) {
            return;
        }
        // 受信直後に latestSequenceNumber を更新（古いフレーム破棄の保証）
        latestSequenceNumber = seq;

        // [🔍 plan02 diag] WebPデータ整合性検証（サンプリング: 10フレームに1回）
        if (ProtocolManager._validationEnabled) {
            ProtocolManager._validationCounter++;
        }
        const shouldValidate = ProtocolManager._validationEnabled && ProtocolManager._validationCounter % 10 === 0;

        if (shouldValidate && !validateWebpData(frameData)) {
            console.warn('[ProtocolManager] Invalid WebP data (validation failed, sampled)');
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
                        console.log(`[ProtocolManager] 3回連続WebP検証失敗 → init要求を送信します (seq=${seq}, init要求回数: ${ProtocolManager._initRequestCount})`);
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
            .then(() => ProtocolManager.decodeAndDraw(frameData, seq))
            .catch(e => {
                console.warn('[ProtocolManager] createImageBitmap decode error:', e.message);
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
                            console.log(`[ProtocolManager] 3回連続デコードエラー → init要求を送信します (seq=${seq}, init要求回数: ${ProtocolManager._initRequestCount})`);
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
                    return;
                }
                // [🔍 plan02 修正] screen オブジェクトへのアクセス前に null チェック
                if (!screen) {
                    console.error('[ProtocolManager] screen is null - draw skipped');
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
};

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
    if (accumulatedBytes < 1024) {
        dataUsage.textContent = `${accumulatedBytes} B`;
    } else if (accumulatedBytes < 1024 * 1024) {
        dataUsage.textContent = `${(accumulatedBytes / 1024).toFixed(1)} KB`;
    } else if (accumulatedBytes < 1024 * 1024 * 1024) {
        dataUsage.textContent = `${(accumulatedBytes / (1024 * 1024)).toFixed(1)} MB`;
    } else {
        dataUsage.textContent = `${(accumulatedBytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
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
        ProtocolManager.syncFps(delay, false);
    }

    return delay;
}

//============================================================================================
// FPSからディレイへの変換共通関数
//============================================================================================
function calculateDelayByFps(fps) {
    if (fps >= 60 || fps <= 0) return 0;
    return Math.round(1000 / fps);
}

//============================================================================================
// 移動制御通信の最適化
//============================================================================================
function sendMoveControl(x, y) {
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
