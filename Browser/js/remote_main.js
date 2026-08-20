//============================================================================================
// remote_main.js
// 初期化・イベントリスナー登録・ダイアログ制御
// 依存: remote_config.js, remote_connection.js, remote_screen.js, remote_input.js, remote_hud.js
//============================================================================================

//============================================================================================
// Viewport CSS変数同期サブシステム (task07 v3 決定版)
//============================================================================================

// [v3対応] TDZ回避のため、モジュール変数は関数の実行より前に定義（let使用）
let viewportSyncRafId = null;

/**
 * #viewport の表示座標を取得し、--viewport-* CSS変数としてドキュメントルートに設定
 * @param {DOMRect|Object|boolean} rectOrImmediate - 座標オブジェクトまたは即時実行フラグ
 * @param {boolean} immediate2 - rect を渡した場合の即時実行フラグ
 *
 * 【v3対応】ダックタイピングで DOMRect 互換を判定（'top' in rectOrImmediate）
 * 期待されるオブジェクト構造: { top, left, right, bottom }
 *
 * 主な呼び出し元と引数パターン:
 * - calculateTogglePosition(): viewport.getBoundingClientRect() の戻り値（DOMRect）を直接渡す
 * - togglePanel() / resetPanelState(): rect を渡さない（immediate=true のみ）
 * - resize / scroll イベント: rect も immediate も渡さない（rAF 同期）
 */
function syncViewportToCSSVariables(rectOrImmediate, immediate2 = false) {
    let immediate = false;
    let rect = null;

    // ダックタイピングで DOMRect 互換を判定（クロスフレーム環境対応）
    if (typeof rectOrImmediate === 'object' && rectOrImmediate !== null && 'top' in rectOrImmediate) {
        rect = rectOrImmediate;
        immediate = immediate2 === true;
    } else {
        immediate = rectOrImmediate === true;
    }

    const update = () => {
        // [task15] 計測対象を #screen-container（ゲーム画面実体）とする
        const screenContainer = document.getElementById('screen-container');
        if (!screenContainer) return;
        // rect が渡された場合は再取得せず使用（二重 getBoundingClientRect 防止）
        const actualRect = rect || screenContainer.getBoundingClientRect();
        const style = document.documentElement.style;
        // Math.round() によるピクセル丸め（sub-pixel レイアウトぼやけ防止）
        style.setProperty('--viewport-top', Math.round(actualRect.top) + 'px');
        style.setProperty('--viewport-left', Math.round(actualRect.left) + 'px');
        style.setProperty('--viewport-right', Math.round(window.innerWidth - actualRect.right) + 'px');
        style.setProperty('--viewport-bottom', Math.round(window.innerHeight - actualRect.bottom) + 'px');
    };

    if (immediate) {
        if (viewportSyncRafId !== null) {
            cancelAnimationFrame(viewportSyncRafId);
            viewportSyncRafId = null;
        }
        update();
    } else {
        if (viewportSyncRafId !== null) return; // 既にフレーム予約済みの場合はスキップ
        viewportSyncRafId = requestAnimationFrame(() => {
            update();
            viewportSyncRafId = null;
        });
    }
}

// 初期化フック
function initViewportSync() {
    syncViewportToCSSVariables(true);
}

if (document.readyState === 'complete' || document.readyState === 'interactive') {
    initViewportSync();
} else {
    document.addEventListener('DOMContentLoaded', initViewportSync);
}

// リサイズ・スクロール時に追従更新（rAF 同期）
window.addEventListener('resize', () => syncViewportToCSSVariables());
window.addEventListener('scroll', () => syncViewportToCSSVariables(), { passive: true });

//============================================================================================
// タイマーフォーマット関数
//============================================================================================
function formatTimer(milliseconds) {
    if (milliseconds < 0) {
        milliseconds = 0;
    }
    const totalSeconds = Math.floor(milliseconds / 1000);
    const seconds = String(totalSeconds).padStart(2, '0');
    const millis = String(Math.floor(milliseconds % 1000)).padStart(3, '0');
    return `${seconds}:${millis}`;
}

//============================================================================================
// 状態判定および state-badge / hud-mini-state のクラス・テキスト更新関数 (v2 DRY化)
// [task04 修正] 接続ステータスガードを削除し、制御モードバッジの専用に統合
//============================================================================================
function updateStateBadge() {
    const stateBadge = document.getElementById('state-badge');
    const hudMiniState = document.getElementById('hud-mini-state');

    const elapsed = Date.now() - lastOperationTime;

    // 自動FPS制御が無効の場合は常に ACTIVE
    if (!autoFpsEnabled) {
        if (stateBadge) {
            stateBadge.textContent = 'ACT';
            stateBadge.className = 'badge';
        }
        if (hudMiniState) {
            hudMiniState.textContent = 'ACT';
        }
        lastSleepSeconds = -1;
        return;
    }

    if (elapsed < ACTIVE_TIMEOUT) {
        if (stateBadge) {
            stateBadge.textContent = 'ACT';
            stateBadge.className = 'badge';
        }
        if (hudMiniState) {
            hudMiniState.textContent = stateBadge ? stateBadge.textContent : 'ACT';
            hudMiniState.className = 'hud-mini-item';
        }
        lastSleepSeconds = -1;
    } else if (elapsed < IDLE_TIMEOUT) {
        const idleElapsed = Math.max(0, elapsed - ACTIVE_TIMEOUT);
        if (stateBadge) {
            stateBadge.textContent = `IDL:${formatTimer(idleElapsed)}`;
            stateBadge.className = 'badge idle';
        }
        if (hudMiniState) {
            hudMiniState.textContent = 'IDL';
        }
        lastSleepSeconds = -1;
    } else {
        const sleepElapsed = Math.max(0, elapsed - IDLE_TIMEOUT);
        const sleepSeconds = Math.floor(sleepElapsed / 1000);

        if (sleepSeconds !== lastSleepSeconds) {
            lastSleepSeconds = sleepSeconds;
            const sleepDisplayElapsed = sleepSeconds * 1000;

            if (stateBadge) {
                stateBadge.textContent = `SLP:${formatTimer(sleepDisplayElapsed)}`;
                stateBadge.className = 'badge sleep';
                void stateBadge.offsetWidth; // リフロー発生によるアニメーション再トリガー
                stateBadge.className = 'badge sleep sleep-pulse';
            }
        }
        if (hudMiniState) {
            hudMiniState.textContent = stateBadge ? stateBadge.textContent : 'SLP';
            hudMiniState.className = 'hud-mini-item';
        }
    }
}

//============================================================================================
// タイマー表示をリアルタイムに更新する独立した高頻度タイマー起動関数
//============================================================================================
function stopTimerUpdate() {
    if (timerUpdateInterval !== null) {
        clearInterval(timerUpdateInterval);
        timerUpdateInterval = null;
    }
}

function startTimerUpdate() {
    // 二重起動防止: 既存タイマーを明示的にクリアしてから再設定
    stopTimerUpdate();
    timerUpdateInterval = setInterval(() => {
        updateStateBadge(); // 集約した単一更新関数を呼ぶ
    }, 100);
}

//============================================================================================
// 設定パネルの開閉トグル
//============================================================================================
const toggleBtn = document.getElementById('settings-toggle');
const settingsPanel = document.getElementById('settings-panel');

function toggleSettings(e) {
    e.preventDefault();
    e.stopPropagation();
    const isClosed = settingsPanel.classList.toggle('settings-closed');
    if (document.body) {
        document.body.classList.toggle('settings-open', !isClosed);
    }
}
toggleBtn.addEventListener('click', toggleSettings);
toggleBtn.addEventListener('pointerdown', e => {
    e.stopPropagation();
});

//============================================================================================
// 設定マスター・ディテール切り替えロジック
//============================================================================================
document.addEventListener('DOMContentLoaded', () => {
    const masterList = document.getElementById('settings-master-list');
    const detailArea = document.getElementById('settings-detail-area');

    if (!masterList || !detailArea) {
        console.warn('settings-master-list or settings-detail-area not found');
        return;
    }

    const masterItems = masterList.querySelectorAll('.master-item');

    if (masterItems.length === 0) {
        console.warn('No master-item elements found');
        return;
    }

    function selectSettingItem(targetId) {
        masterItems.forEach(item => item.classList.remove('active'));
        const activeItem = masterList.querySelector(`.master-item[data-target="${targetId}"]`);
        if (activeItem) activeItem.classList.add('active');

        const panels = detailArea.querySelectorAll('.detail-panel');
        panels.forEach(panel => panel.classList.remove('active'));
        const targetPanel = detailArea.querySelector(`#detail-${targetId}`);
        if (targetPanel) targetPanel.classList.add('active');
    }

    masterItems.forEach(item => {
        item.addEventListener('click', () => {
            selectSettingItem(item.dataset.target);
        });
    });

    selectSettingItem(masterItems[0].dataset.target);

    const scrollUpBtn = document.getElementById('settings-scroll-up');
    const scrollDownBtn = document.getElementById('settings-scroll-down');

    if (scrollUpBtn && scrollDownBtn && masterItems.length > 0) {
        const scrollAmount = 22;

        scrollUpBtn.addEventListener('click', () => {
            masterList.scrollBy({ top: -scrollAmount, behavior: 'smooth' });
        });
        scrollDownBtn.addEventListener('click', () => {
            masterList.scrollBy({ top: scrollAmount, behavior: 'smooth' });
        });
    }
});

//============================================================================================
// ボタンの初期状態の動的適用と切り替えイベントの登録ユーティリティ
//============================================================================================
function initSettingsButtons(groupId, currentValue, callback) {
    const group = document.getElementById(groupId);
    if (!group) return;
    const buttons = group.querySelectorAll('.settings-btn');

    buttons.forEach(btn => {
        const fpsAttr = btn.getAttribute('data-fps');
        const qualAttr = btn.getAttribute('data-quality');
        const autoAttr = btn.getAttribute('data-auto-fps');
        const sensAttr = btn.getAttribute('data-sensitivity');
        const zoomAttr = btn.getAttribute('data-zoom');
        const filterAttr = btn.getAttribute('data-filter');
        const dragDistAttr = btn.getAttribute('data-drag-distance');
        const tapTimeoutAttr = btn.getAttribute('data-tap-timeout');

        let btnValue;
        if (fpsAttr !== null) {
            btnValue = parseFloat(fpsAttr);
        } else if (qualAttr !== null) {
            btnValue = parseInt(qualAttr, 10);
        } else if (autoAttr !== null) {
            btnValue = parseInt(autoAttr, 10);
        } else if (sensAttr !== null) {
            btnValue = parseFloat(sensAttr);
        } else if (zoomAttr !== null) {
            btnValue = parseFloat(zoomAttr);
        } else if (filterAttr !== null) {
            btnValue = parseInt(filterAttr, 10);
        } else if (dragDistAttr !== null) {
            btnValue = parseInt(dragDistAttr, 10);
        } else if (tapTimeoutAttr !== null) {
            btnValue = parseInt(tapTimeoutAttr, 10);
        }

        if (btnValue === currentValue) {
            btn.classList.add('active');
        } else {
            btn.classList.remove('active');
        }

        btn.addEventListener('click', () => {
            buttons.forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            callback(btnValue);
        });
    });
}

//============================================================================================
// 各設定ボタンの初期化
//============================================================================================
initSettingsButtons('fps-group', targetFps, value => {
    targetFps = value;
    localStorage.setItem('targetFps', value);
    dataFps = value.toString();
    const delay = calculateDelayByFps(targetFps);
    ProtocolManager.syncFps(delay);
});

initSettingsButtons('quality-group', targetQuality, value => {
    targetQuality = value;
    localStorage.setItem('targetQuality', value);
    fetch(`/control?action=set_quality&q=${value}`);
});
// 起動時に現在の画質設定をC#側に確実に伝播させる初期化送信
fetch(`/control?action=set_quality&q=${targetQuality}`);

const autoFpsInitialValue = autoFpsEnabled ? 1 : 0;
initSettingsButtons('auto-fps-group', autoFpsInitialValue, value => {
    autoFpsEnabled = value === 1;
    localStorage.setItem('autoFpsEnabled', value.toString());

    lastOperationTime = Date.now();
    lastSleepSeconds = -1;

    // 共通関数を使用して #state-badge と #hud-mini-state の両方を更新
    updateStateBadge();

    clearTimeout(updateTimer);
    updateTimer = setTimeout(updateScreen, 50);
});

initSettingsButtons('sensitivity-group', targetSensitivity, value => {
    targetSensitivity = value;
    localStorage.setItem('targetSensitivity', value.toString());
});

//============================================================================================
// 通信プロトコル設定
//============================================================================================
(function initProtocolButtons() {
    const group = document.getElementById('protocol-group');
    if (!group) return;

    const buttons = group.querySelectorAll('.settings-btn');
    const currentMode = ProtocolManager.currentMode;

    buttons.forEach(btn => {
        const mode = btn.getAttribute('data-protocol');

        if (mode === currentMode) {
            btn.classList.add('active');
        } else {
            btn.classList.remove('active');
        }

        btn.addEventListener('click', () => {
            buttons.forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            ProtocolManager.setMode(mode);
            // [task19 Bug 1 fix] In the case of WCS mode, call switchToWcsMode() to reset the VideoDecoder state
            // [Round 7 Major 修正] switchToWcsMode() は VideoDecoder 状態リセットのみを行う（WS 送信は関数内にない）。
            // setMode('webcodecs') → startConnection() → closeWebSocket() + attemptWebSocket() により
            // 新 WS は CONNECTING 状態のため、switch_mode webcodecs の実際の送信は接続確立後の
            // onopen ハンドラ (remote_connection.js) で行われる。
            if (mode === 'webcodecs' && typeof switchToWcsMode === 'function') {
                switchToWcsMode();
            }
        });
    });
})();

// ページロード時に ProtocolManager を初期化
ProtocolManager.init();

//============================================================================================
// HUDバー位置設定（上付き / 右付き）
//============================================================================================
function setHudBarPosition(mode) {
    const validModes = ['right', 'top', 'left', 'bottom'];
    if (!validModes.includes(mode)) mode = 'right';
    hudBarPosition = mode;
    localStorage.setItem('hudBarPosition', mode);

    const hudControlBar = document.getElementById('hud-control-bar');
    const viewport = document.getElementById('viewport');

    if (hudControlBar) {
        validModes.forEach(m => {
            hudControlBar.classList.toggle(`position-${m}`, mode === m);
        });
    }

    if (viewport) {
        validModes.forEach(m => {
            viewport.classList.toggle(`hud-pos-${m}`, mode === m);
        });
    }

    const group = document.getElementById('hud-position-group');
    if (group) {
        const buttons = group.querySelectorAll('.settings-btn');
        buttons.forEach(btn => {
            btn.classList.toggle('active', btn.getAttribute('data-hud-pos') === mode);
        });
    }

    syncViewportToCSSVariables(true);

    // [task19 不具合2修正] HUD バー位置変更で #game-screen の実寸が変わるため rect キャッシュを無効化
    if (typeof window.invalidateGameScreenRect === 'function') {
        window.invalidateGameScreenRect();
    }
}

(function initHudPositionButtons() {
    const group = document.getElementById('hud-position-group');
    if (group) {
        const buttons = group.querySelectorAll('.settings-btn');
        buttons.forEach(btn => {
            btn.addEventListener('click', () => {
                const pos = btn.getAttribute('data-hud-pos');
                setHudBarPosition(pos);
            });
        });
    }
    // 初期適用
    setHudBarPosition(hudBarPosition);
})();

// [task06] スリープ復帰およびタブフォーカス復帰時の即時画面更新トリガー
document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') {
        triggerResumeUpdate();
    }
});
window.addEventListener('focus', () => {
    triggerResumeUpdate();
});

// [task06] 初期化時にも即座に呼び出してSLEEP状態から脱出
// ページリロード時はvisibilityStateが既に'versible'のためvisibilitychangeイベントが発火しない
triggerResumeUpdate();

initSettingsButtons('zoom-group', targetZoom, value => {
    targetZoom = value;
    localStorage.setItem('targetZoom', value.toString());
    drawMagnifier();
});

//============================================================================================
// ドラッグ距離・タップ時間の初期化
//============================================================================================
initSettingsButtons('drag-distance-group', dragThreshold, value => {
    dragThreshold = value;
    localStorage.setItem('dragThreshold', value.toString());
});

initSettingsButtons('tap-timeout-group', tapTimeout, value => {
    tapTimeout = value;
    localStorage.setItem('tapTimeout', value.toString());
});

//============================================================================================
// 確認モーダル制御
//============================================================================================
const confirmModalOverlay = document.getElementById('confirm-modal-overlay');
const confirmModalMessage = document.getElementById('confirm-modal-message');
const confirmModalOk = document.getElementById('confirm-modal-ok');
const confirmModalCancel = document.getElementById('confirm-modal-cancel');
let confirmModalCallback = null;

function showConfirmModal(message, callback) {
    confirmModalMessage.textContent = message;
    confirmModalCallback = callback;
    confirmModalOverlay.classList.add('show');
}

function hideConfirmModal() {
    confirmModalOverlay.classList.remove('show');
    confirmModalCallback = null;
}

confirmModalOk.addEventListener('click', () => {
    if (confirmModalCallback) confirmModalCallback();
    hideConfirmModal();
});

confirmModalCancel.addEventListener('click', () => {
    hideConfirmModal();
});

confirmModalOverlay.addEventListener('click', (e) => {
    if (e.target === confirmModalOverlay) {
        hideConfirmModal();
    }
});

//============================================================================================
// ログインページ移動ボタン
//============================================================================================
const btnNavigateLogin = document.getElementById('btn-navigate-login');
btnNavigateLogin.addEventListener('click', (e) => {
    e.stopPropagation();
    showConfirmModal('ログインページへ移動しますか？', () => {
        fetch('/control?action=navigate_login');
    });
});

//============================================================================================
// ログアウト（空白ページ）ボタン
// 確認メッセージは既存 btn-navigate-login と同様の「○○へ移動しますか？」形式に統一
//（二次レビュー Minor 3 対応）
//============================================================================================
const btnLogoutBlank = document.getElementById('btn-logout-blank');
btnLogoutBlank.addEventListener('click', (e) => {
    e.stopPropagation();
    showConfirmModal('ログアウトして空白ページへ移動しますか？', () => {
        fetch('/control?action=logout_blank');
    });
});

//============================================================================================
// フィルター・暗視の初期化
//============================================================================================
// ページロード時にフィルターを初期適用
applyClientFilter(currentFilterMode);

initSettingsButtons('filter-group', currentFilterMode, value => {
    currentFilterMode = value;
    localStorage.setItem('filterMode', value.toString());
    applyClientFilter(value);
});

// 暗視フィルター初期化
applyNightvisionFilter(currentNightvisionIndex);

const btnNightvision = document.getElementById('btn-nightvision');
function updateNightvisionButton() {
    btnNightvision.textContent = nightvisionModes[currentNightvisionIndex].name;
}
updateNightvisionButton();

btnNightvision.addEventListener('click', (e) => {
    e.stopPropagation();
    currentNightvisionIndex = (currentNightvisionIndex + 1) % nightvisionModes.length;
    localStorage.setItem('nightvisionIndex', currentNightvisionIndex.toString());
    applyNightvisionFilter(currentNightvisionIndex);
    updateNightvisionButton();
});

//============================================================================================
// 初期化処理
//============================================================================================
// リアルタイムタイマー更新ループを起動
startTimerUpdate();

// 画面更新ループを起動
updateScreen();

// HUD同期を起動
updateHud();

//============================================================================================
// 初期カーソル表示
//============================================================================================
window.addEventListener('resize', updateCursorDisplay);
updateCursorDisplay();

//============================================================================================
// 受領中任務HUDのトグルタイマー
//============================================================================================
setInterval(() => {
    window._showQuestProgress = !window._showQuestProgress;
    const rows = document.querySelectorAll('.quest-slot-row');
    rows.forEach(row => {
        if (!row.classList.contains('empty-quest-row')) {
            if (window._showQuestProgress) {
                row.classList.add('show-progress');
            } else {
                row.classList.remove('show-progress');
            }
        }
    });
}, 5000);

//============================================================================================
// ゲーム画面誤クリック防止フラグ（グローバル定義）
//============================================================================================
window.isDismissingPanel = false;

//============================================================================================
// 横画面用：操作パネルの折り畳み/展開トグルおよびゲーム画面タップ消去制御
// 【クリティカルレビュー指摘1,2反映】transitionend廃止 + 状態管理簡素化
//============================================================================================
(function initTrackpadPanel() {
    const toggleBtn = document.getElementById('trackpad-toggle');
    const trackpadArea = document.getElementById('trackpad-area');
    const hudControlBar = document.getElementById('hud-control-bar');
    const viewport = document.getElementById('viewport');

    if (!trackpadArea || !hudControlBar) return;

    /**
     * パネルの展開/閉合を切り替える
     */
    function togglePanel(e) {
        if (e) {
            e.preventDefault();
            e.stopPropagation();
        }

        const isExpanded = trackpadArea.classList.toggle('panel-expanded');
        hudControlBar.classList.toggle('panel-expanded', isExpanded);
        if (toggleBtn) {
            toggleBtn.classList.toggle('panel-expanded', isExpanded);
            toggleBtn.setAttribute('aria-expanded', isExpanded ? 'true' : 'false');
        }

        // bodyにもpanel-expandedを同期付与（:has()非対応ブラウザ用CSSセレクタの起点）
        document.body.classList.toggle('panel-expanded', isExpanded);

        if (viewport) {
            viewport.classList.toggle('overlay-active', isExpanded);
            viewport.classList.toggle('panel-expanded', isExpanded);
        }

        // viewport マージン変更を即座にCSS変数へ反映
        syncViewportToCSSVariables(true);

        // [task19 不具合2修正] パネル展開/閉合で #game-screen の実寸が変わるため rect キャッシュを無効化
        if (typeof window.invalidateGameScreenRect === 'function') {
            window.invalidateGameScreenRect();
        }
    }

    hudControlBar.addEventListener('click', togglePanel);
    hudControlBar.addEventListener('pointerdown', e => e.stopPropagation());
    if (toggleBtn) {
        toggleBtn.addEventListener('click', togglePanel);
        toggleBtn.addEventListener('pointerdown', e => e.stopPropagation());
    }

    /**
     * パネル状態を閉合にリセットする
     */
    function resetPanelState() {
        trackpadArea.classList.remove('panel-expanded');
        hudControlBar.classList.remove('panel-expanded');
        if (toggleBtn) {
            toggleBtn.classList.remove('panel-expanded');
            toggleBtn.setAttribute('aria-expanded', 'false');
        }
        // bodyからもpanel-expandedを削除
        document.body.classList.remove('panel-expanded');
        if (viewport) {
            viewport.classList.remove('overlay-active');
            viewport.classList.remove('panel-expanded');
        }

        // パネルリセットに伴う viewport マージン変更を即座にCSS変数へ反映
        syncViewportToCSSVariables(true);

        // [task19 不具合2修正] パネルリセットに伴い #game-screen の実寸が変わるため rect キャッシュを無効化
        if (typeof window.invalidateGameScreenRect === 'function') {
            window.invalidateGameScreenRect();
        }
    }

    /**
     * ゲーム画面タッチ/クリックでHUDを非表示にする（initTrackpadPanel IIFE 内定義）
     *
     * スコープ: initTrackpadPanel IIFE 内定義、window.dismissIfExpanded としてグローバル公開
     * resetPanelState() は同一 IIFE 内の関数として参照可能（クリティカルレビュー指摘1対応）
     *
     * 注意（クリティカルレビュー指摘2対応）:
     * resetPanelState() の中で isDismissingPanel = false を実行してはならない。
     * isDismissingPanel のリセットは touchend ハンドラおよび orientationchange ハンドラに限定する。
     *
     * 注意（クリティカルレビュー指摘3対応）:
     * タッチ操作（touchstart）の場合のみ isDismissingPanel = true を設定。
     * マウス操作（click）では touchend が発火しないためフラグは不要。
     */
    function dismissIfExpanded(e) {
        // ガード節: 展開中でない場合は早期リターン
        if (!trackpadArea || !trackpadArea.classList.contains('panel-expanded')) return false;

        // タッチ操作（touchstart）の場合のみガードフラグを立てる（クリティカルレビュー指摘3対応）
        if (e && e.type === 'touchstart') {
            window.isDismissingPanel = true;
        }

        // 既存の resetPanelState() を呼び出し、ロジックの共通化を図る
        // resetPanelState() 内で isDismissingPanel を変更しない（クリティカルレビュー指摘2対応）
        resetPanelState();

        // イベントを消費する（誤タップ防止）
        if (e && e.cancelable) {
            e.preventDefault();
        }

        return true;
    }

    // 外部（remote_input.js 等）からアクセス可能にするためグローバル公開
    window.dismissIfExpanded = dismissIfExpanded;

    // [Task10 v2] 外部（initHudPosition IIFE 等）からアクセス可能にするためグローバル公開
    window.resetPanelState = resetPanelState;

    // クリックイベント（マウス操作時、フォールバック用）
    // touchstart は remote_input.js 側で処理するため、click のみを登録（クリティカルレビュー指摘4対応）
    if (viewport) {
        viewport.addEventListener('click', dismissIfExpanded);
    }

    // 画面回転時の状態クリーンアップ
    function handleOrientationChange() {
        if (orientationDebounceTimer) clearTimeout(orientationDebounceTimer);
        orientationDebounceTimer = setTimeout(() => {
            // ガードフラグのクリア
            window.isDismissingPanel = false;

            // パネル状態のリセット
            if (typeof resetPanelState === 'function') {
                resetPanelState();
            }
        }, ORIENTATION_DEBOUNCE_MS); // 100msデバウンス
    }

    if (screen && screen.orientation) {
        screen.orientation.addEventListener('change', handleOrientationChange);
    }
    window.addEventListener('orientationchange', handleOrientationChange);
})();

//============================================================================================
// [Task10 v2] 画面回転デバウンス用タイマー
//============================================================================================
let orientationDebounceTimer = null;

const ORIENTATION_DEBOUNCE_MS = 100; // 画面回転デバウンス時間（ms）
