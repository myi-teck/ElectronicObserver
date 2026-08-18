//=============================================================================================
// remote_screen.js
// Canvas差分WebP描画・拡大鏡・画面フィルター
// 依存: remote_config.js, remote_connection.js
//=============================================================================================

// DOM要素の取得（ページ読み込み時に実行される）
const screen = document.getElementById('game-screen');

// [task19 v5] Critical 4 修正: screen 変数が宣言されている remote_screen.js 側で rect キャッシュを定義・公開
// script 読み込み順: remote_screen.js → remote_input.js → remote_connection.js
// remote_screen.js が screen 変数を所有し、remote_input.js が getCachedGameScreenRect() を呼び出す
let _cachedGameScreenRect = null;
let _gameScreenRectCacheValid = false;

function getCachedGameScreenRect() {
    if (!_gameScreenRectCacheValid || _cachedGameScreenRect === null) {
        _cachedGameScreenRect = screen.getBoundingClientRect();
        _gameScreenRectCacheValid = true;
    }
    return _cachedGameScreenRect;
}

// remote_input.js および他のモジュールから参照可能に window に公開
window.getCachedGameScreenRect = getCachedGameScreenRect;

// [task19 不具合2修正] レイアウト変更時に rect キャッシュを無効化する公開関数
// resize イベントだけでなく、HUD バー表示/非表示・パネル展開等で #game-screen の実寸が変わる
// 場合にもキャッシュを無効化し、stale な rect による座標ズレを防止する
function invalidateGameScreenRect() {
    _gameScreenRectCacheValid = false;
}
window.invalidateGameScreenRect = invalidateGameScreenRect;

// [task19 v3] resize イベントでキャッシュを無効化
if (typeof window.addEventListener === 'function') {
    window.addEventListener('resize', () => { invalidateGameScreenRect(); });
}
let gameCtx = screen.getContext('2d');
const viewport = document.getElementById('viewport');
const status = document.getElementById('conn-status');
const fpsInfo = document.getElementById('fps-info');
const dataUsage = document.getElementById('data-usage');
const debug = document.getElementById('debug-info');
const cursor = document.getElementById('virtual-cursor');

// 拡大スコープ用Canvas要素および描画コンテキストの取得
const magCanvas = document.getElementById('magnifier-canvas');
const magCtx = magCanvas ? magCanvas.getContext('2d') : null;

//=============================================================================================
// 処理ブロック名 : updateScreen
// 概要           : 画面更新ルーティングを行う（差分WebP配信へ一本化）
//=============================================================================================
async function updateScreen() {
    await updateScreenDiff();
}

//=============================================================================================
// 処理ブロック名 : updateScreenDiff
// 概要           : 差分WebP方式で画面を更新する（HTTPモード用）
//=============================================================================================
async function updateScreenDiff() {
    const now = Date.now();

    // [task06] フォールバック: タイマースリップ検知
    // 前回のフレーム実行から (予定ディレイ + 5秒) 以上経過している場合、PCスリープから復帰したとみなす
    // 注: lastFrameStartTime は「最後に更新処理が**開始**された時刻」を記録する。
    // スリープ復帰検知には「関数呼出間隔」で十分であり、非同期処理の完了時刻ではない。
    const elapsedSinceLastFrame = now - lastFrameStartTime;
    if (elapsedSinceLastFrame > (lastScheduledDelay + SLEEP_SLIP_TOLERANCE_MS)) {
        console.log(`[SleepResume] Timer slip detected: elapsed=${elapsedSinceLastFrame}ms, scheduled=${lastScheduledDelay}ms`);
        // [v4] 現在すでにupdateScreenDiff()が実行中であるため、triggerResumeUpdate()を呼ばない。
        // 直接 isFirstWebpRequest = true をセットし、自身のフローでキーフレームを取得する。
        isFirstWebpRequest = true;
        if (typeof ProtocolManager !== 'undefined' && ProtocolManager.effectiveMode === 'ws') {
            ProtocolManager.sendWsControl({ action: 'init' });
        }
    }
    lastFrameStartTime = now;

    // [v4] ヘルパー: スリープ復帰ペンディングがある場合は即時ディレイ (0ms) を適用
    const getNextDelay = (calculatedDelay) => {
        if (triggerResumeUpdate._pendingImmediate) {
            triggerResumeUpdate._pendingImmediate = false;
            return 0;
        }
        return calculatedDelay;
    };

    // WebSocket モード時は HTTP ポーリングをスキップ
    if (ProtocolManager.effectiveMode === 'ws') {
        isUpdating = false;
        const nextDelay = getNextDelay(getDynamicDelay());
        lastScheduledDelay = nextDelay; // [task06] 予定ディレイを記録
        updateTimer = setTimeout(updateScreenDiff, nextDelay);
        return;
    }

    if (isUpdating) return;
    isUpdating = true;

    const startTime = Date.now();

    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), 2000);

    try {
        let url = `/screen.webp?t=${Date.now()}`;
        // [v4] URL生成時に isFirstWebpRequest を消費（レスポンス完了時の無条件上書きを防止）
        if (isFirstWebpRequest) {
            url += '&init=1';
            isFirstWebpRequest = false;
        }
        const response = await fetch(url, { signal: controller.signal });
        clearTimeout(timeoutId);

        if (response.status === 401) {
            console.warn("Screen: Session expired. Redirecting to login...");
            window.location.href = '/login-page';
            return;
        }

        if (response.status === 204) {
            if (typeof updateDataUsage === 'function') {
                updateDataUsage(getResponseHeaderBytes(response));
            }
            // [v4] isFirstWebpRequest の消費はURL生成時に行うため、ここでの無条件上書きは削除
            // isFirstWebpRequest = false; // ← 削除
            isUpdating = false;

            const elapsed = Date.now() - startTime;
            const nextDelay = getNextDelay(Math.max(5, getDynamicDelay() - elapsed));
            lastScheduledDelay = nextDelay; // [task06] 予定ディレイを記録
            updateTimer = setTimeout(updateScreenDiff, nextDelay);
            return;
        }

        if (!response.ok) throw new Error(`Fetch failed: ${response.status}`);

        // [v4] isFirstWebpRequest の消費はURL生成時に行うため、ここでの無条件上書きは削除
        // isFirstWebpRequest = false; // ← 削除 (79行目)

        const rawSeqHeader = response.headers.get('X-Frame-Sequence');
        const httpSeq = (rawSeqHeader !== null && !isNaN(parseInt(rawSeqHeader, 10)))
            ? parseInt(rawSeqHeader, 10)
            : -1;
        const blob = await response.blob();

        updateDataUsage(blob.size + getResponseHeaderBytes(response));

        const img = new Image();

        // [v5] img.onload 全体を try...finally で保護（描画例外によるデッドロック防止）
        img.onload = () => {
            try {
                if (httpSeq >= 0) {
                    const diff = (httpSeq - latestSequenceNumber) | 0;
                    if (diff <= 0 && latestSequenceNumber !== 0) {
                        return;
                    }
                    latestSequenceNumber = httpSeq;
                }

                gameCtx.clearRect(0, 0, screen.width, screen.height);
                gameCtx.drawImage(img, 0, 0);

                drawMagnifier();

                status.textContent = 'SYNCHRONIZED';
                status.classList.add('ok');

                frameCount++;
                updateFpsDisplay();
            } catch (err) {
                console.error("[remote_screen] img.onload render error:", err);
            } finally {
                // [v5] 例外発生時も確実にリソース解放・更新フラグ解除・タイマー再予約
                URL.revokeObjectURL(img.src);
                isUpdating = false;

                const elapsed = Date.now() - startTime;
                const nextDelay = getNextDelay(Math.max(5, getDynamicDelay() - elapsed));
                lastScheduledDelay = nextDelay; // [task06] 予定ディレイを記録
                updateTimer = setTimeout(updateScreenDiff, nextDelay);
            }
        };

        img.onerror = () => {
            URL.revokeObjectURL(img.src);
            isUpdating = false;
            // [v4] isFirstWebpRequest は維持。次回の updateScreenDiff() でキーフレーム要求によりリカバリ
            // [v5 重要] _pendingImmediate の消費方針:
            // 画像ロードエラー時は「一度更新を試みた」とみなし、getNextDelay() を経由して
            // _pendingImmediate フラグを消費（false にリセット）する。
            // これにより、エラー発生後の次回のタイマー予約は即時ディレイ (0ms) が適用されるが、
            // その後のサイクルは通常の getDynamicDelay() に基づく遅延に戻される。
            // 理由: エラー時は「更新の試行は完了」であり、次回の即時更新を維持するよりも
            // 通常のタイマーサイクルに任せてサーバー負荷を抑制する設計が適切。
            const elapsed = Date.now() - startTime;
            const nextDelay = getNextDelay(Math.max(5, getDynamicDelay() - elapsed));
            lastScheduledDelay = nextDelay; // [task06] 予定ディレイを記録
            updateTimer = setTimeout(updateScreenDiff, nextDelay);
        };

        img.src = URL.createObjectURL(blob);

    } catch (error) {
        clearTimeout(timeoutId);
        isFirstWebpRequest = true; // 例外発生時のみ次回リトライでキーフレーム要求
        status.textContent = 'RE-SYNCING...';
        status.classList.remove('ok');
        isUpdating = false;

        const nextDelay = getNextDelay(1000);
        lastScheduledDelay = nextDelay; // [task06] 固定値1000msのため1000を記録
        updateTimer = setTimeout(updateScreenDiff, nextDelay);
    }
}

//=============================================================================================
// 処理ブロック名 : updateFpsDisplay
//=============================================================================================
function updateFpsDisplay() {
    const now = Date.now();
    const elapsed = now - lastFpsUpdate;
    if (elapsed >= 1000) {
        const fps = ((frameCount * 1000) / elapsed).toFixed(2);
        if (fpsInfo) fpsInfo.textContent = `${fps} FPS`;

        // ミニ情報に直接反映（system-monitor-barと完全一致）
        const hudMiniFps = document.getElementById('hud-mini-fps');
        if (hudMiniFps) {
            const fpsText = `${fps} FPS`; // e.g. "21.45 FPS"
            if (hudMiniFps.textContent !== fpsText) {
                hudMiniFps.textContent = fpsText;
            }
        }

        frameCount = 0;
        lastFpsUpdate = now;
    }
}

//=============================================================================================
// 処理ブロック名 : drawMagnifier
//=============================================================================================
function drawMagnifier() {
    if (!magCtx || !screen || screen.width === 0) return;

    const container = document.querySelector('.magnifier-container');
    if (!container) return;

    const rect = container.getBoundingClientRect();
    const canvasWidth = rect.width || 72;
    const canvasHeight = rect.height || 34;

    if (magCanvas.width !== canvasWidth || magCanvas.height !== canvasHeight) {
        magCanvas.width = canvasWidth;
        magCanvas.height = canvasHeight;
    }

    const screenRect = screen.getBoundingClientRect();
    const displayWidth = screenRect.width || 1;

    const cropDisplayWidth = canvasWidth / targetZoom;
    const cropWidth = cropDisplayWidth * (screen.width / displayWidth);

    const cropHeight = cropWidth * (canvasHeight / canvasWidth);

    const cursorPixelX = vCursorX * screen.width;
    const cursorPixelY = vCursorY * screen.height;

    let idealSx = cursorPixelX - (cropWidth / 2);
    let idealSy = cursorPixelY - (cropHeight / 2);

    let actualCropWidth = Math.min(cropWidth, screen.width);
    let actualCropHeight = Math.min(cropHeight, screen.height);

    let sx = Math.max(0, Math.min(idealSx, screen.width - actualCropWidth));
    let sy = Math.max(0, Math.min(idealSy, screen.height - actualCropHeight));

    const cursorInCropX = cursorPixelX - sx;
    const cursorInCropY = cursorPixelY - sy;

    const targetInCanvasX = canvasWidth / 2;
    const targetInCanvasY = canvasHeight / 2;

    const scaleX = canvasWidth / actualCropWidth;
    const scaleY = canvasHeight / actualCropHeight;

    const dx = targetInCanvasX - cursorInCropX * scaleX;
    const dy = targetInCanvasY - cursorInCropY * scaleY;

    magCtx.clearRect(0, 0, canvasWidth, canvasHeight);
    magCtx.drawImage(
        screen,
        sx, sy, actualCropWidth, actualCropHeight,
        dx, dy, canvasWidth, canvasHeight
    );
}

//=============================================================================================
// 仮想カーソル表示＆拡大鏡描画の requestAnimationFrame 同期化
//=============================================================================================
function scheduleCursorUpdate() {
    if (isCursorUpdatePending) return;
    isCursorUpdatePending = true;
    requestAnimationFrame(() => {
        isCursorUpdatePending = false;
        updateCursorDisplay();
    });
}

function updateCursorDisplay() {
    // [task19 v5] #game-screen 基準に統一: containerRect のオフセット計算を削除
    // 入力正規化と描画逆変換が同一 rect を使用することで整合性を保証
    const rect = getCachedGameScreenRect();
    // 入力正規化: x = (clientX - rect.left) / rect.width
    // 描画逆変換: px = rect.left + vCursorX * rect.width
    // 両者は互いに逆関数であり、整合性が数学的に保証される
    const x = rect.left + (vCursorX * rect.width);
    const y = rect.top + (vCursorY * rect.height);
    cursor.style.left = x + 'px';
    cursor.style.top = y + 'px';
    debug.textContent = `POS-X: ${Math.round(vCursorX * 100)}% POS-Y: ${Math.round(vCursorY * 100)}%`;
    drawMagnifier();
}

//=============================================================================================
// プライバシーフィルター設定（クライアントサイド適用版）
//=============================================================================================
function applyClientFilter(mode) {
    const gameScreen = document.getElementById('game-screen');
    gameScreen.classList.remove('filter-grayscale', 'filter-lowcontrast', 'filter-greenscale-night', 'filter-greenscale-military', 'filter-greenscale-phosphor');
    switch (mode) {
        case 1:
            gameScreen.classList.add('filter-grayscale');
            break;
        case 4:
            gameScreen.classList.add('filter-lowcontrast');
            break;
        default:
            break;
    }
}

//=============================================================================================
// 暗視フィルター設定（トグルサイクル方式）
//=============================================================================================
function applyNightvisionFilter(index) {
    const gameScreen = document.getElementById('game-screen');
    nightvisionModes.forEach(m => { if (m.className) gameScreen.classList.remove(m.className); });
    const mode = nightvisionModes[index];
    if (mode.className) {
        gameScreen.classList.add(mode.className);
    }
}
