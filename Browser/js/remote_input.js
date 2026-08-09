//============================================================================================
// remote_input.js
// トラックパッド・タップ・マウスイベント制御
// 依存: remote_config.js, remote_connection.js, remote_screen.js
//============================================================================================

// ゲーム画面タッチ/マウス操作
let viewportTouchStartX = 0;

// [task15] イベントバブリングガード: #hud-control-bar 上の操作を無視（バブリング防止）
function isHudControlBarEvent(e) {
    return e.target && e.target.closest('#hud-control-bar') !== null;
}

let viewportTouchStartY = 0;
let viewportTouchStartTime = 0;
let isDraggingOnViewport = false;
let isMouseDownOnViewport = false;

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
    const rect = screen.getBoundingClientRect();
    // [Task17] ゼロサイズ保護ガード: rect.width または rect.height が 0 の場合は処理をスキップ
    if (rect.width === 0 || rect.height === 0) return;
    const x = Math.max(0, Math.min(1.0, (clientX - rect.left) / rect.width));
    const y = Math.max(0, Math.min(1.0, (clientY - rect.top) / rect.height));
    vCursorX = x;
    vCursorY = y;
    scheduleCursorUpdate();
    sendMoveControl(x, y);
}

// viewport touchstart
// 【二次レビュー💡指摘3対応】 passive: false について:
// このイベントはタッチサイクル中のゲーム操作防止（preventDefault）のために passive: false を必要とする。
// touch-action: none が #viewport に設定されている場合、デフォルトアクションは既に無効化されており、
// preventDefault() の呼び出しは安全である。一部のブラウザで「passive:false イベントで preventDefault を
// 呼んだがデフォルトアクションが存在しない」警告が出る可能性があるが、機能的な影響はない。
viewport.addEventListener('touchstart', (e) => {
    // [task15] HUDバー上の操作を無視（バブリング防止）
    if (isHudControlBarEvent(e)) return;

    // HUD展開中のタップであれば、パネルをたたんでゲームアクション送信をスキップ
    // dismissIfExpanded は remote_main.js 側の initTrackpadPanel IIFE 内で定義・公開
    if (typeof window.dismissIfExpanded === 'function' && window.dismissIfExpanded(e)) {
        return;
    }

    const touch = e.touches[0];
    viewportTouchStartX = touch.clientX;
    viewportTouchStartY = touch.clientY;
    viewportTouchStartTime = Date.now();
    isDraggingOnViewport = false;
    activateViewportFpsControl();
    handleViewportMove(touch.clientX, touch.clientY);
}, { passive: false });

// viewport touchmove
viewport.addEventListener('touchmove', (e) => {
    // [task15] HUDバー上の操作を無視（バブリング防止）
    if (isHudControlBarEvent(e)) return;

    const touch = e.touches[0];
    const dx = touch.clientX - viewportTouchStartX;
    const dy = touch.clientY - viewportTouchStartY;
    const dist = Math.sqrt(dx * dx + dy * dy);

    if (dist > dragThreshold && !isDraggingOnViewport) {
        isDraggingOnViewport = true;
        sendControlAction('mousedown');
    }

    handleViewportMove(touch.clientX, touch.clientY);
}, { passive: true });

// viewport touchend
viewport.addEventListener('touchend', (e) => {
    // [task15] HUDバー上の操作を無視（バブリング防止）
    if (isHudControlBarEvent(e)) return;

    // 注意: touchend では dismissIfExpanded(e) を呼ばない（二次レビュー💡指摘4対応）。
    // パネル折り畳みは touchstart/click 時点で完了しているため、touchend では
    // isDismissingPanel フラグによるゲームアクションのキャンセルのみを行う。
    // isDismissingPanel のリセットはここで実行（クリティカルレビュー指摘2対応）。
    if (window.isDismissingPanel) {
        window.isDismissingPanel = false;
        return;
    }

    const elapsed = Date.now() - viewportTouchStartTime;

    if (isDraggingOnViewport) {
        sendControlAction('mouseup');
    } else if (elapsed <= tapTimeout) {
        const rect = screen.getBoundingClientRect();
        const x = Math.max(0, Math.min(1.0, (viewportTouchStartX - rect.left) / rect.width));
        const y = Math.max(0, Math.min(1.0, (viewportTouchStartY - rect.top) / rect.height));
        sendControlAction('touch_click', x, y);
    }

    isDraggingOnViewport = false;
}, { passive: true });

// viewport mousedown
viewport.addEventListener('mousedown', (e) => {
    if (e.button !== 0) return;

    // [task15] HUDバー上の操作を無視（バブリング防止）
    if (isHudControlBarEvent(e)) return;

    // HUD展開中のクリックであれば、パネルをたたんでゲームアクション送信をスキップ
    // dismissIfExpanded は remote_main.js 側の initTrackpadPanel IIFE 内で定義・公開
    if (typeof window.dismissIfExpanded === 'function' && window.dismissIfExpanded(e)) {
        return;
    }

    e.preventDefault();

    isMouseDownOnViewport = true;
    viewportTouchStartX = e.clientX;
    viewportTouchStartY = e.clientY;
    viewportTouchStartTime = Date.now();

    sendControlAction('mousedown');
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
    if (!isMouseDownOnViewport) return;
    isMouseDownOnViewport = false;

    sendControlAction('mouseup');

    const elapsed = Date.now() - viewportTouchStartTime;
    const dx = e.clientX - viewportTouchStartX;
    const dy = e.clientY - viewportTouchStartY;
    const dist = Math.sqrt(dx * dx + dy * dy);

    if (elapsed <= tapTimeout && dist <= dragThreshold) {
        // タップ判定
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
    sendControlAction('click');
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
