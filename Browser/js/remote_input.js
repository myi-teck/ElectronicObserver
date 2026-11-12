//============================================================================================
// remote_input.js
// トラックパッド・タップ・マウスイベント制御
// 依存: remote_config.js, remote_connection.js, remote_screen.js
//============================================================================================

// ゲーム画面タッチ/マウス操作
let viewportTouchStartX = 0;

// イベントバブリングガード: #hud-control-bar 上の操作を無視（バブリング防止）
function isHudControlBarEvent(e) {
    return e.target && e.target.closest('#hud-control-bar') !== null;
}

let viewportTouchStartY = 0;
let viewportTouchStartTime = 0;
let isDraggingOnViewport = false;
let isMouseDownOnViewport = false;
let lastTouchEndTime = 0; // 合成マウスイベントの時間窓判定用

function activateViewportFpsControl() {
    const isSlowFpsMode = (Date.now() - lastOperationTime) >= ACTIVE_TIMEOUT;
    lastOperationTime = Date.now();
    if (isSlowFpsMode) {
        clearTimeout(updateTimer);
        updateTimer = setTimeout(updateScreen, 50);
    }
    const stateBadge = document.getElementById('state-badge');
    stateBadge.textContent = 'ACT';
    stateBadge.className = 'badge';
}

function handleViewportMove(clientX, clientY) {
    // remote_screen.js に定義された window.getCachedGameScreenRect() を呼び出す
    // 本関数は content rect（object-fit: contain の letterbox 適用後）を返すため、
    // 以下の正規化は content（可視ゲーム領域）基準 0〜1 となる（サーバー GetBrowserCoords の
    // content 基準前提 RemoteController.cs:2705-2749 と整合）。letterbox 領域（黒帯）を指した
    // 場合はクランプ（0/1）で content 端に丸める
    const rect = window.getCachedGameScreenRect();
    // ゼロサイズ保護ガード: rect.width または rect.height が 0 の場合は処理をスキップ
    if (rect.width === 0 || rect.height === 0) return;
    const x = Math.max(0, Math.min(1.0, (clientX - rect.left) / rect.width));
    const y = Math.max(0, Math.min(1.0, (clientY - rect.top) / rect.height));
    vCursorX = x;
    vCursorY = y;
    scheduleCursorUpdate();
    sendMoveControl(x, y);
    // イベントログ計装: touchstart/touchmove/mousemove/trackpad の全経路で通る
    //（スロットリングは remote_eventlog.js 内で行う）
    if (window.__eventLog) window.__eventLog.moveThrottled('mousemove', x, y);
}

// viewport touchstart
// passive: false について:
// このイベントはタッチサイクル中のゲーム操作防止（preventDefault）のために passive: false を必要とする。
// 注意: touch-action: none（remote_base.css:96）はスクロール/ピンチズームのみ無効化し、
// 合成マウスイベント（mousedown/mouseup/click）は抑制しない（MDN/W3C 仕様）。
// 合成マウスイベントの抑制は （touchstart での e.preventDefault()）で対応する。
viewport.addEventListener('touchstart', (e) => {
    // HUDバー上の操作を無視（バブリング防止）
    if (isHudControlBarEvent(e)) return;

    // HUD展開中のタップであれば、パネルをたたんでゲームアクション送信をスキップ
    // dismissIfExpanded は remote_main.js 側の initTrackpadPanel IIFE 内で定義・公開
    if (typeof window.dismissIfExpanded === 'function' && window.dismissIfExpanded(e)) {
        return;
    }

    // 合成マウスイベント（mousedown/mouseup/click）を抑制する。
    // 注意: touch-action: none（remote_base.css:96）は合成マウスイベントを抑制しない
    //（タッチのデフォルト動作＝スクロール・ピンチズームのみ無効化する CSS プロパティ）。
    // W3C Touch Events Level 2 仕様: touchstart/touchmove/touchend いずれかが
    // preventDefault() でキャンセルされると、同一タッチインタラクションのマウス系
    // イベント（mousedown/mouseup/click）は発火しない。passive: false 登録済み
    //であるため preventDefault() は有効に動作する。
    e.preventDefault();

    const touch = e.touches[0];
    viewportTouchStartX = touch.clientX;
    viewportTouchStartY = touch.clientY;
    viewportTouchStartTime = Date.now();
    isDraggingOnViewport = false;
    activateViewportFpsControl();
    handleViewportMove(touch.clientX, touch.clientY);
    // イベントログ計装: handleViewportMove 後の正規化座標 vCursorX/vCursorY を記録
    //（rect が未利用（ゼロサイズ guard）の場合は前回値になる点に注意）
    if (window.__eventLog) window.__eventLog.gesture('touchstart', e, vCursorX, vCursorY);
}, { passive: false });

// viewport touchmove
viewport.addEventListener('touchmove', (e) => {
    // HUDバー上の操作を無視（バブリング防止）
    if (isHudControlBarEvent(e)) return;

    const touch = e.touches[0];
    const dx = touch.clientX - viewportTouchStartX;
    const dy = touch.clientY - viewportTouchStartY;
    const dist = Math.sqrt(dx * dx + dy * dy);

    if (dist > dragThreshold && !isDraggingOnViewport) {
        isDraggingOnViewport = true;
        const rect = window.getCachedGameScreenRect();
        const sx = (rect.width > 0) ? Math.max(0, Math.min(1.0, (viewportTouchStartX - rect.left) / rect.width)) : 0.5;
        const sy = (rect.height > 0) ? Math.max(0, Math.min(1.0, (viewportTouchStartY - rect.top) / rect.height)) : 0.5;
        sendControlAction('mousedown', sx, sy);
    }

    handleViewportMove(touch.clientX, touch.clientY);
}, { passive: true });

// viewport touchend
viewport.addEventListener('touchend', (e) => {
    // HUDバー上の操作を無視（バブリング防止）
    if (isHudControlBarEvent(e)) return;
    lastTouchEndTime = Date.now(); // touchend 時刻記録（合成マウスイベント時間窓用）

    // 注意: touchend では dismissIfExpanded(e) を呼ばない。
    // パネル折り畳みは touchstart/click 時点で完了しているため、touchend では
    // isDismissingPanel フラグによるゲームアクションのキャンセルのみを行う。
    // isDismissingPanel のリセットはここで実行。
    if (window.isDismissingPanel) {
        window.isDismissingPanel = false;
        return;
    }

    const elapsed = Date.now() - viewportTouchStartTime;
    const rect = window.getCachedGameScreenRect();
    const x = (rect.width > 0) ? Math.max(0, Math.min(1.0, (viewportTouchStartX - rect.left) / rect.width)) : 0.5;
    const y = (rect.height > 0) ? Math.max(0, Math.min(1.0, (viewportTouchStartY - rect.top) / rect.height)) : 0.5;

    vCursorX = x;
    vCursorY = y;
    scheduleCursorUpdate();

    // イベントログ計装（正規化座標 x/y を記録）＋ gesture 終了時の move 区間サマリー flush
    if (window.__eventLog) {
        window.__eventLog.gesture('touchend', e, x, y);
        window.__eventLog.flushOnGestureEnd();
    }

    if (isDraggingOnViewport) {
        sendControlAction('mouseup', vCursorX, vCursorY);
    } else if (elapsed <= tapTimeout) {
        // touchend 座標計算を #game-screen 基準に統一（handleViewportMove と同じ）
        sendControlAction('touch_click', x, y);
    }

    isDraggingOnViewport = false;
}, { passive: true });

// viewport mousedown
viewport.addEventListener('mousedown', (e) => {
    if (e.button !== 0) return;

    // HUDバー上の操作を無視（バブリング防止）
    if (isHudControlBarEvent(e)) return;

    // touchend 後 touchMouseGuardMs 以内のマウスイベントは合成とみなして無視。
    // 合成マウスイベントは isTrusted === true で実マウスと区別できないため時間窓で判定。
    // 位置: button/HUD チェック通過後（HUD バー上操作を時間窓で誤爆させないため）。
    if (Date.now() - lastTouchEndTime < touchMouseGuardMs) return;

    // HUD展開中のクリックであれば、パネルをたたんでゲームアクション送信をスキップ
    // dismissIfExpanded は remote_main.js 側の initTrackpadPanel IIFE 内で定義・公開
    if (typeof window.dismissIfExpanded === 'function' && window.dismissIfExpanded(e)) {
        return;
    }

    e.preventDefault(); // 既存（実マウス用・テキスト選択抑制）を維持

    isMouseDownOnViewport = true;
    viewportTouchStartX = e.clientX;
    viewportTouchStartY = e.clientY;
    viewportTouchStartTime = Date.now();

    // 計装用座標: rect 未利用（ゼロサイズ guard）時は null
    let x = null;
    let y = null;
    const rect = window.getCachedGameScreenRect();
    if (rect.width > 0 && rect.height > 0) {
        x = Math.max(0, Math.min(1.0, (e.clientX - rect.left) / rect.width));
        y = Math.max(0, Math.min(1.0, (e.clientY - rect.top) / rect.height));
        vCursorX = x;
        vCursorY = y;
        scheduleCursorUpdate();
        sendControlAction('mousedown', x, y);
    } else {
        sendControlAction('mousedown');
    }
    // イベントログ計装（P2 ガード通過後＝実マウスと判定されたときのみ記録）
    if (window.__eventLog) window.__eventLog.gesture('mousedown', e, x, y);
    activateViewportFpsControl();
    handleViewportMove(e.clientX, e.clientY);
});

// viewport mousemove
viewport.addEventListener('mousemove', (e) => {
    if (!isMouseDownOnViewport) return;
    handleViewportMove(e.clientX, e.clientY);
});

// viewport mouseup
viewport.addEventListener('mouseup', (e) => {
    // 二重安全: 通常は合成 mousedown が P2 で無効化されるため
    // isMouseDownOnViewport が false となり早期 return する。
    // ここは実装差（mousedown なしで mouseup のみ発火するケース）に対する最終防御。
    if (Date.now() - lastTouchEndTime < touchMouseGuardMs) return;
    if (!isMouseDownOnViewport) return;
    isMouseDownOnViewport = false;

    const elapsed = Date.now() - viewportTouchStartTime;
    const dx = e.clientX - viewportTouchStartX;
    const dy = e.clientY - viewportTouchStartY;
    const dist = Math.sqrt(dx * dx + dy * dy);

    const rect = window.getCachedGameScreenRect();
    const x = (rect.width > 0) ? Math.max(0, Math.min(1.0, (e.clientX - rect.left) / rect.width)) : vCursorX;
    const y = (rect.height > 0) ? Math.max(0, Math.min(1.0, (e.clientY - rect.top) / rect.height)) : vCursorY;

    // イベントログ計装
    if (window.__eventLog) window.__eventLog.gesture('mouseup', e, x, y);

    if (elapsed <= tapTimeout && dist <= dragThreshold) {
        // 短時間クリック（タップ判定）: touch_click で確実に発火
        sendControlAction('touch_click', x, y);
    } else {
        sendControlAction('mouseup', x, y);
    }
});

//============================================================================================
// 範囲選択防止イベント（CSS + JSの3層防御）
//============================================================================================
document.addEventListener('contextmenu', e => {
    e.preventDefault();
    return false;
}, { passive: false });

document.addEventListener('selectstart', e => {
    e.preventDefault();
    return false;
}, { passive: false });

document.addEventListener('dragstart', e => {
    e.preventDefault();
    return false;
}, { passive: false });

//============================================================================================
// トラックパッド操作（ポインタキャプチャ制御付き）
//============================================================================================
const pad = document.getElementById('trackpad');
const hudContainerElement = document.getElementById('hud-container');

let lastX = 0, lastY = 0;
let isTrackpadDragging = false;

function handleTrackpadPointerDown(e) {
    // 右端16pxエリア（明示的スクロールバー領域）でのタッチはスクロール操作に委ねる
    if (hudContainerElement) {
        const rect = hudContainerElement.getBoundingClientRect();
        if (e.clientX >= rect.right - 16) {
            return; // 右端明示的スクロールバー専用領域（幅12px + 誤触防止4px）
        }
    }

    // ボタン要素などのクリック操作は通過
    if (e.target && (e.target.tagName === 'BUTTON' || e.target.closest('button'))) {
        return;
    }

    lastX = e.clientX;
    lastY = e.clientY;
    isTrackpadDragging = true;

    // ⚠️ CRITICAL FIX: e.targetではなく安定した親コンテナ pad (#trackpad) に対して
    // setPointerCapture を適用する。
    if (pad && pad.setPointerCapture) {
        try {
            pad.setPointerCapture(e.pointerId);
        } catch (err) {
            console.error('pad.setPointerCapture failed:', err);
            if (window.__eventLog) window.__eventLog.consoleLog('error', 'pad.setPointerCapture failed: ' + err, 'INPUT_PTR_CAP_FAIL');
        }
    }

    if (magnifierTimer) {
        clearTimeout(magnifierTimer);
    }

    magnifierTimer = setTimeout(() => {
        const magnifier = document.querySelector('.magnifier-container');
        if (magnifier) {
            magnifier.classList.add('visible');
        }
    }, 1000);
}

function handleTrackpadPointerMove(e) {
    // 🚨 厳格なガード: isTrackpadDragging が false の場合は一切処理しない
    if (!isTrackpadDragging) {
        return;
    }

    // 右端16pxエリア（明示的スクロールバー領域）でのタッチはスクロール操作に委ねる
    if (hudContainerElement) {
        const rect = hudContainerElement.getBoundingClientRect();
        if (e.clientX >= rect.right - 16) {
            return; // 右端明示的スクロールバー専用領域
        }
    }

    const isSlowFpsMode = (Date.now() - lastOperationTime) >= ACTIVE_TIMEOUT;
    lastOperationTime = Date.now();

    if (isSlowFpsMode) {
        clearTimeout(updateTimer);
        updateTimer = setTimeout(updateScreen, 50);
    }
    const stateBadge = document.getElementById('state-badge');
    stateBadge.textContent = 'ACT';
    stateBadge.className = 'badge';
    const dx = e.clientX - lastX;
    const dy = e.clientY - lastY;
    lastX = e.clientX;
    lastY = e.clientY;

    const baseSensitivity = 0.002;
    const sensitivity = baseSensitivity * targetSensitivity;
    vCursorX = Math.max(0, Math.min(1.0, vCursorX + dx * sensitivity));
    vCursorY = Math.max(0, Math.min(1.0, vCursorY + dy * sensitivity));

    scheduleCursorUpdate();
    sendMoveControl(vCursorX, vCursorY);
}

function handleTrackpadPointerUp(e) {
    // releasePointerCapture の明示的呼び出し（MDN推奨）
    if (pad && pad.releasePointerCapture) {
        try {
            pad.releasePointerCapture(e.pointerId);
        } catch (err) {
            // NotFoundError は既に解放済みの場合の正常系
            if (err.name !== 'NotFoundError') {
                console.error('releasePointerCapture failed:', err);
                if (window.__eventLog) window.__eventLog.consoleLog('error', 'releasePointerCapture failed: ' + err, 'INPUT_PTR_RELEASE_FAIL');
            }
        }
    }
    isTrackpadDragging = false;

    if (magnifierTimer) {
        clearTimeout(magnifierTimer);
        magnifierTimer = null;
    }
    const magnifier = document.querySelector('.magnifier-container');
    if (magnifier) {
        magnifier.classList.remove('visible');
    }
}

// 親要素（#trackpad）のみでイベントを一元管理
if (pad) {
    pad.addEventListener('pointerdown', handleTrackpadPointerDown);
    pad.addEventListener('pointermove', handleTrackpadPointerMove);
    pad.addEventListener('pointerup', handleTrackpadPointerUp);
    pad.addEventListener('pointercancel', handleTrackpadPointerUp);
}

// ウィンドウフォーカス喪失時のドラッグ状態リセット
window.addEventListener('blur', () => {
    isTrackpadDragging = false;
    // 拡大鏡タイマーのクリア（フォーカス喪失時に残り続けるのを防止）
    if (magnifierTimer) {
        clearTimeout(magnifierTimer);
        magnifierTimer = null;
    }
});

/* クリーンアップ関数 */
function cleanupTrackpadListeners() {
    if (pad) {
        pad.removeEventListener('pointerdown', handleTrackpadPointerDown);
        pad.removeEventListener('pointermove', handleTrackpadPointerMove);
        pad.removeEventListener('pointerup', handleTrackpadPointerUp);
        pad.removeEventListener('pointercancel', handleTrackpadPointerUp);
    }
}

/* ページアンロード時のクリーンアップ */
window.addEventListener('unload', cleanupTrackpadListeners);

//============================================================================================
// クリックボタン操作
//============================================================================================
const clickBtn = document.getElementById('click-btn');
const clickBtnElement = document.getElementById('click-btn');

clickBtnElement.addEventListener('pointerdown', e => {
    e.preventDefault();
    clickBtnElement.classList.add('clicked');

    const isSlowFpsMode = (Date.now() - lastOperationTime) >= ACTIVE_TIMEOUT;
    lastOperationTime = Date.now();

    if (isSlowFpsMode) {
        clearTimeout(updateTimer);
        updateTimer = setTimeout(updateScreen, 50);
    }
    const stateBadge = document.getElementById('state-badge');
    stateBadge.textContent = 'ACT';
    stateBadge.className = 'badge';
    sendControlAction('click', vCursorX, vCursorY);
});

clickBtnElement.addEventListener('pointerup', e => {
    clickBtnElement.classList.remove('clicked');
});

clickBtnElement.addEventListener('pointercancel', e => {
    clickBtnElement.classList.remove('clicked');
});

//============================================================================================
// HUDタップトグルとトラックパッドドラッグ操作の両立メカニズム
// 移動閾値（8px）によるタップ/ドラッグ分岐ロジック
//============================================================================================
(function() {
    /**
     * HUD要素のタップ/ドラッグ判定を設定する
     * e.stopPropagation() を廃止し、イベントを背後の #trackpad へ通過させる
     * @param {string} elementId - 対象要素のID
     * @param {Function} toggleCallback - タップ時に実行するコールバック
     */
    function setupHudTapToggle(elementId, toggleCallback) {
        const el = document.getElementById(elementId);
        if (!el) return;

        let startX = 0;
        let startY = 0;
        let startTime = 0;
        let isCanceled = false;

        el.addEventListener('pointerdown', (e) => {
            // e.stopPropagation() は呼ばない！ (トラックパッドへのイベント伝播を維持)
            startX = e.clientX;
            startY = e.clientY;
            startTime = Date.now();
            isCanceled = false;
        });

        el.addEventListener('pointermove', (e) => {
            if (isCanceled) return;
            const dx = e.clientX - startX;
            const dy = e.clientY - startY;
            const dist = Math.sqrt(dx * dx + dy * dy);

            // HUD_TAP_THRESHOLD_PX px以上の移動があればドラッグ（トラックパッドカーソル移動）と判定し、タップトグルをキャンセル
            if (dist >= HUD_TAP_THRESHOLD_PX) {
                isCanceled = true;
            }
        });

        el.addEventListener('pointerup', (e) => {
            if (isCanceled) return;
            const elapsed = Date.now() - startTime;
            const dx = e.clientX - startX;
            const dy = e.clientY - startY;
            const dist = Math.sqrt(dx * dx + dy * dy);

            // 移動距離HUD_TAP_THRESHOLD_PX px未満 かつ HUD_TAP_TIMEOUT_MS ms以内のタップのみトグル実行
            if (dist < HUD_TAP_THRESHOLD_PX && elapsed <= HUD_TAP_TIMEOUT_MS) {
                toggleCallback();
            }
        });
    }

    // 艦隊ステータスHUD: タップで艦隊切替
    setupHudTapToggle('fleet-hud', () => {
        switchFleetDetail();
    });

    // 艦隊詳細HUD: タップで装備/HP表示切り替え
    setupHudTapToggle('fleet-detail-panel', () => {
        window._showFleetEquipment = !window._showFleetEquipment;
        renderFleetDetail(currentDetailFleetId);
    });
})();
