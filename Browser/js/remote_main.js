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
// [task25] body 高さの visual viewport 追従（iOS layout/visual viewport 乖離対策）
// 根本原因: iOS（viewport-fit=cover）は layout viewport（100dvh / 100% の基準）が
//   visual viewport（実表示領域）より高くなり、iOS は visual viewport を layout 中央に
//   配置するため body 上下端が (layout高さ − 可視高さ) / 2 ずつ隠れる。
// 修正: body 高さを visual viewport 高さに揃え、左右配置時のゲームviewportが実表示内に収まるようする。
//============================================================================================
function syncBodyHeightToVisualViewport() {
    // [P-C1] document が完全に active でないとき、MDN 仕様上:
    //   - Window.visualViewport は null
    //   - VisualViewport.height は 0
    //   を返す。null と <=0 の両方をガードし window.innerHeight にフォールバックする
    //   （0px を body に書き込み画面を消失させる致命エラーを防止）。
    const vv = window.visualViewport;
    const vvh = (vv && typeof vv.height === 'number') ? vv.height : NaN;
    const rawHeight = (Number.isFinite(vvh) && vvh > 0) ? vvh : window.innerHeight;
    // [S-3①] 上限クランプ: visual viewport が layout viewport を超える異常拡大
    //   （ピンチズーム等）を防止し、body が layout viewport（innerHeight）を超えないことを保証。
    //   Math.round は sub-pixel レイアウトぼやけ防止（既存 syncViewportToCSSVariables と同方針）。
    const newHeight = Math.min(Math.round(rawHeight), window.innerHeight) + 'px';
    // [S-3②] inline body.style.height ではなく、documentElement の CSS 変数 --kc-body-height
    //   に書き込む。body { height: var(--kc-body-height, 100svh) } が吸収し、
    //   変数未設定（JS 未実行・非サポート）時は 100svh（静的下限）がフォールバックする。
    const root = document.documentElement;
    // [再書込みガード] 値が変わった時のみ書き込み（不要な style 書き込み・layout thrashing 回避）
    if (root.style.getPropertyValue('--kc-body-height') !== newHeight) {
        root.style.setProperty('--kc-body-height', newHeight);
    }
    // [task25 補足] body 高さ変更 → #game-screen の rect が変化。window.resize は発火しないため、
    // remote_screen.js の rect キャッシュを明示的に無効化し座標ズレを防止。
    // [P-C2 検証済み] remote_screen.js は remote_main.js より先読み込み（RemoteController.html:415-420）
    // かつ window.invalidateGameScreenRect が公開済み（remote_screen.js:34）。typeof ガードは予防。
    if (typeof window.invalidateGameScreenRect === 'function') {
        window.invalidateGameScreenRect();
    }
}

// 初期適用（load 時・remote_screen.js が invalidateGameScreenRect を公開した後に実行）
// [S-5] remote_main.js は RemoteController.html:420 で </body> 直前（末尾）に読み込まれるため、
//   本関数実行時点で document.body / document.documentElement の存在が確定している。
syncBodyHeightToVisualViewport();

// [task25] visual viewport の resize / scroll で再適用（iOS のキーボード・アドレスバー表示/隠蔽・回転等）
if (window.visualViewport) {
    // resize: visual viewport の寸法変化（キーボード表示/隠蔽・回転等）で発火。再適用の主経路。
    window.visualViewport.addEventListener('resize', syncBodyHeightToVisualViewport);
    // [P-M1] scroll: iOS ではキーボード展開時等に visual viewport のオフセット/寸法がスクロール連動で
    //   変化しうるため登録。再書込みガード（値変化時のみ書き込み）があるため、高さ変化なしで発火しても
    //   実害は皆無。オフセット変化シナリオの検知を目的に残す。
    //   [review r1 Suggestion 1 反映] 既存の window scroll リスナ（81行目）と同様 { passive: true } を指定し、
    //   ブラウザのスクロール最適化（コンポジタスレッドのスムーズスクロール）を阻害しない。
    //   本関数は preventDefault() を呼ばないため passive 指定は安全。
    window.visualViewport.addEventListener('scroll', syncBodyHeightToVisualViewport, { passive: true });
}

// [task25] 既存の window.resize で再適用。
// [S-7] これは既存 resize リスナ（remote_main.js:80、syncViewportToCSSVariables）とは
//   独立した「並列登録」であり、置換ではない。同一の resize イベントで両リスナが独立に発火する。
window.addEventListener('resize', syncBodyHeightToVisualViewport);

// [task25] iOS はレイアウト更新が数回に分かれるため、orientationchange で 0/100/300ms の
// setTimeout で複数回再適用。
// [P-N3] 各タイミングの意図（iOS は orientationchange 後にレイアウト更新が複数回に分かれるため）:
//   - 0ms   = イベントループの次のタスクで即時再適用（iOS はイベント直後に第1回レイアウト更新を行う）
//   - 100ms = 中間レイアウト更新後の再適用
//   - 300ms = レイアウト更新完了後の最終再適用
//   0ms は「同期的に即座に」ではなく「イベントループに回して次タスクで」実行する意図を持つ。
// [S-1] この orientationchange リスナは、既存 handleOrientationChange（remote_main.js:814、
//   パネル状態リセット・100ms デバウンス）とは独立に「意図的な並列登録」する（両立・非置換・責務分離）。
//   回転時に handleOrientationChange（パネルリセット）と本関数（body 高さ追従）が別コールバックで発火する。
window.addEventListener('orientationchange', () => {
    syncBodyHeightToVisualViewport();
    [0, 100, 300].forEach((ms) => {
        setTimeout(syncBodyHeightToVisualViewport, ms);
    });
});

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
        if (window.__eventLog) window.__eventLog.consoleLog('warn', 'settings-master-list or settings-detail-area not found', 'MASTER_LIST_NOT_FOUND');
        return;
    }

    const masterItems = masterList.querySelectorAll('.master-item');

    if (masterItems.length === 0) {
        console.warn('No master-item elements found');
        if (window.__eventLog) window.__eventLog.consoleLog('warn', 'No master-item elements found', 'MASTER_ITEM_EMPTY');
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
            // プロトコル再選択はユーザー操作（auto-FPS 降格を解除し「再選択してもフリーズが粘着する」状態を防ぐ）
            lastOperationTime = Date.now();
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

//============================================================================================
// auto-FPS 降格の可視化: IDLE/SLEEP 降格中、既存の fps 表示部に有効レートを追記する（新要素なし）。
// 「意図的な省電力降格」と「故障によるフリーズ」をユーザーが区別できるようにする。
// updateFpsDisplay は毎秒テキストを再書き込みするため、ラップして再書き込み後に追記し、
// ラベルと表示がテキストを取り合わないよう idempotent に付与・除去を同一経路で行う
//============================================================================================
(function wrapFpsDisplayWithAutoLabel() {
    if (typeof window.updateFpsDisplay !== 'function' || typeof getDynamicDelay !== 'function') return;
    const original = window.updateFpsDisplay;
    window.updateFpsDisplay = function() {
        original.apply(this, arguments);
        // getDynamicDelay() が 2000ms / 60000ms を返している間が IDLE / SLEEP 降格状態
        const delay = getDynamicDelay();
        let label = null;
        if (delay === 2000) label = '0.5 (auto)';
        else if (delay === 60000) label = '1/60 (auto)';
        const targets = [
            document.getElementById('fps-info'),
            document.getElementById('hud-mini-fps')
        ];
        targets.forEach(el => {
            if (!el) return;
            const base = el.textContent.replace(/ \[[^\]]*\]$/, '');
            el.textContent = label ? base + ' [' + label + ']' : base;
        });
    };
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
        // [task14] viewport click を計装（§3.4: dismissIfExpanded をラップして計装。gesture('click', e, null, null)）
        viewport.addEventListener('click', (e) => {
            if (window.__eventLog) window.__eventLog.gesture('click', e, null, null);
            return dismissIfExpanded(e);
        });
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

//=============================================================================================
// ダミー映像バッジ（conn-status と独立の span — updateConnStatus の textContent 上書きで
// 消去されないため、接続状態遷移とバッジ表示が競合しない）。
// 表示は 5s ポーリング（checkLoginState）＋ onopen 時 /capture-config fetch で追従し、
// OFF では非表示に戻る（新要素・新タイマーなし — 既存ポーリングループに載せる）。
//=============================================================================================
var _dummyBadgeLast = null; // 直近表示状態（null=未確定）。遷移時のみイベントログへ記録し 5s ポーリングの重複記録を防ぐ
function setDummyBadge(on) {
    var el = document.getElementById('hud-mini-dummy');
    if (!el) return;
    on = !!on;
    el.style.display = on ? '' : 'none';
    if (on === _dummyBadgeLast) return;
    _dummyBadgeLast = on;
    if (window.__eventLog) window.__eventLog.consoleLog('warn', '検証用ダミー映像: ' + (on ? 'ON' : 'OFF'), on ? 'DUMMY_ON' : 'DUMMY_OFF');
}

//=============================================================================================
// [task22] ログイン状態ポーリング（5 秒間隔）
// 本体ブラウザがログアウト空白ページ状態の間、/logout-page へフルページリダイレクトする
// （「ログイン中フラグが false ならログアウト中画面にリダイレクト」要件の実装）。
// WS モードでは remote_screen.js の HTTP ポーリングがスキップされ
// 画面フレームも届かないため、このポーリングが HTTP/WS 両モードで機能する唯一の状態同期手段。
// 401（リモート認証セッション切れ）は既存挙動と同様に /login-page へリダイレクト。
//=============================================================================================
function checkLoginState() {
    fetch('/login-state', { cache: 'no-store' })
        .then(function (res) {
            if (res.status === 401) {
                window.location.href = '/login-page';
                return null;
            }
            if (!res.ok) throw new Error('HTTP ' + res.status);
            return res.json();
        })
        .then(function (data) {
            if (typeof setDummyBadge === 'function') {
                setDummyBadge(!!(data && data.dummyVideo === true));
            }
            if (data && data.loggedOut === true) {
                window.location.href = '/logout-page';
            }
        })
        .catch(function (e) {
            // [task22 E2E 修正レビュー SS-E2E-2] 例外の握りつぶしをしない（プロジェクトルールの「サイレントな例外の握りつぶし禁止」に準拠）:
            // エラー内容をコンソールに warn 出力し、開発者ツールで可視化する（5 秒ポーリングで繰り返し発火する可能性はあるが、
            // パーサー・通信障害の可観測性向上のため 1 行の warn に留める）。
            // 一時的な通信エラーは次回のポーリングで再試行（updateScreenDiff の catch と同方針）
            console.warn('[task22] /login-state poll failed:', e);
            if (window.__eventLog) window.__eventLog.consoleLog('warn', '[task22] /login-state poll failed: ' + (e && e.message ? e.message : e), 'LOGIN_STATE_POLL_FAIL');
        });
}
setInterval(checkLoginState, 5000);
checkLoginState();
