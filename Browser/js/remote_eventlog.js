// remote_eventlog.js
// クリック・タッチイベント専用ログ
// DOM イベント（gesture）と送信 action を収集し、サーバへ中継する。
// 前提: ProtocolManager（remote_connection.js:23-26）が存在することを確認済み
//（effectiveMode: 'ws'/'http'・）。
(function () {
    'use strict';
    let lastMoveLogTime = 0;
    // move スロットリングの「区間サマリー」状態（取りこぼし防止）
    // スロットルで省略された move を集約し、区間終了（次回の発火時 or gesture 終了時）に する。
    let _moveSummary = null; // { kind, count, lastX, lastY, startTs, endTs }

    // TAG 単位重複排除状態。
    // 同一 TAG が TAG_DEDUP_MS 以内に再中継される場合は省略し、省略件数を _tagSuppressed に集約する。
    // C1/C5（接続状態・再接続）系 TAG は重複排除対象外（重要な診断情報は必ず中継）。
    const TAG_DEDUP_MS = 1000;
    const TAG_DEDUP_EXEMPT = new Set([
        'WS_CONNECT', 'WS_OPEN', 'WS_CLOSE', 'WS_CLOSE_FAIL', 'WS_INTENTIONAL_CLOSE',
        'WS_ERROR', 'WS_MAX_RECONNECT', 'WS_RECONNECT', 'FALLBACK_HTTP', 'PONG_TIMEOUT', 'HTTP_HUD_INIT'
    ]);
    const _tagLastRelay = new Map(); // TAG → 前回中継 ts（Date.now）
    const _tagSuppressed = new Map(); // TAG → フラッシュ未済の省略件数
    const _tagFlushTimer = {};        // TAG → フラッシュ待機タイマー
    function scheduleSuppressedFlush(t) {
        // 省略件数が蓄積した TAG は 10 秒以内に必ずサマリー行を flush する。
        if (_tagFlushTimer[t]) return;
        _tagFlushTimer[t] = setTimeout(() => {
            _tagFlushTimer[t] = null;
            const suppressed = _tagSuppressed.get(t) || 0;
            if (suppressed > 0) {
                _tagSuppressed.set(t, 0);
                relay({ kind: 'console', level: 'info', tag: t, msg: '[suppressed] ' + t + ' x' + suppressed, route: currentRoute() });
            }
        }, 10000);
    }

    function flushMoveSummary(reason) {
        // 蓄積された省略 move を 1 行の区間サマリーとして中継する（取りこぼし防止）。
        if (!_moveSummary) return;
        const s = _moveSummary;
        _moveSummary = null;
        relay({
            kind: 'move_summary',
            suppressed: s.count,
            lastX: (s.lastX === undefined ? null : s.lastX),
            lastY: (s.lastY === undefined ? null : s.lastY),
            span_ms: s.endTs - s.startTs,
            startTs: s.startTs,
            endTs: s.endTs,
            reason: reason // 'interval' | 'gesture_end' | 'flush'
        });
    }

    function currentRoute() {
        // ProtocolManager.effectiveMode は remote_connection.js:26 で定義済み（値: 'ws'/'http'）。
        // 初期値 nullや例外時は 'http' にフォールバック（既存 HTTP モードと一致）。
        try { return (ProtocolManager.effectiveMode === 'ws') ? 'ws' : 'http'; }
        catch (e) { return 'http'; }
    }

    function formatLocal(d) {
        // yyyy-MM-dd HH:mm:ss.fff（ローカル時刻・ms 3桁）—スキーマと統一。
        // 手動フォーマット: toLocaleString の fractionalSecondDigits は古いエンジンで無視され
        //（MDN 参照）、ロケール依存の差分があるため不採用。
        const p = (n, w) => String(n).padStart(w === undefined ? 2 : w, '0');
        return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()) +
            ' ' + p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds()) +
            '.' + p(d.getMilliseconds(), 3);
    }

    function relay(payload) {
        const now = new Date();
        payload.ts_ms = now.getTime();
        payload.ts_local = formatLocal(now);   // スキーマ統一（toISOString は誤り）
        payload.src = 'client';
        if (typeof ProtocolManager !== 'undefined' && ProtocolManager.effectiveMode === 'ws') {
            // WebSocket モード: 既存制御チャネルに乗せて中継。
            // ev は JSON **文字列**で送る（サーバが ExtractJsonString で抽出するため）。
            ProtocolManager.sendWsControl({ action: 'event_log', ev: JSON.stringify(payload) });
        } else {
            // HTTP モード: 専用エンドポイントへ POST（JSON body・1 行ずつ個別 POST・バッチ化しない）
            fetch('/event-log', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            }).catch((err) => {
                // AGENTS.md NEVER 対応: 空 catch 禁止。サーバ未到達時にログできる唯一の経路は
                // ブラウザコンソールであるため、console.warn で記録（処理は継続・best-effort）。
                console.warn('[remote_eventlog] event_log POST failed:', err);
            });
        }
    }

    window.__eventLog = {
        // DOM イベント（gesture）を記録
        gesture(type, e, x, y) {
            relay({
                kind: 'gesture', type: type,
                x: (x === undefined ? null : x), y: (y === undefined ? null : y),
                clientX: (e ? e.clientX : null), clientY: (e ? e.clientY : null),
                isTrusted: (e ? e.isTrusted : null),
                route: currentRoute()
            });
        },
        // 送信した action を記録
        action(action, x, y) {
            relay({
                kind: 'action', action: action,
                x: (x === undefined ? null : x), y: (y === undefined ? null : y),
                route: currentRoute()
            });
        },
        // move 系は高頻度のためスロットリングして記録（remote_config.js の eventLogMoveThrottleMs）
        // スロットリング時の取りこぼし防止: 省略された move は区間サマリーとして記録する
        moveThrottled(kind, x, y) {
            const interval = (typeof eventLogMoveThrottleMs === 'number' && eventLogMoveThrottleMs > 0)
                ? eventLogMoveThrottleMs : 100;
            const now = Date.now();
            if (now - lastMoveLogTime < interval) {
                // スロットル発動: この move は個々には記録せず、区間サマリーに集約する。
                if (_moveSummary) {
                    _moveSummary.count++;
                    _moveSummary.endTs = now;
                    if (x !== undefined) _moveSummary.lastX = x;
                    if (y !== undefined) _moveSummary.lastY = y;
                } else {
                    _moveSummary = { kind: kind, count: 1, lastX: (x === undefined ? null : x), lastY: (y === undefined ? null : y), startTs: now, endTs: now };
                }
                return;
            }
            lastMoveLogTime = now;
            // スロットルを通過: まず前区間（前回通過以降に省略された move）のサマリーを flush する。
            flushMoveSummary('interval');
            relay({ kind: kind, x: (x === undefined ? null : x), y: (y === undefined ? null : y), route: currentRoute() });
        },
        // gesture 終了時（touchend/mouseup）に呼ばれる: 残留する区間サマリーを flush する（取りこぼし防止）。
        // lastMoveLogTime = 0 リセットは「gesture 境界でスロットリング状態を引き継がない」
        //（次の gesture の最初の move は必ず記録される）ための設計（取りこぼしなし）。
        flushOnGestureEnd() {
            flushMoveSummary('gesture_end');
            lastMoveLogTime = 0;
        },
        // JS 診断ログ
        // TAG 単位重複排除: 同一 TAG が 1000ms 以内に再中継された場合は省略。
        // C1/C5（接続状態・再接続）系 TAG は重複排除対象外。省略件数は _tagSuppressed に集約し、
        // 次の同 TAG 中継時または 10 秒タイマーで "[suppressed] TAG xN" サマリー行を記録する（取りこぼし防止）。
        // tag が null（未指定）の呼び出しは重複排除対象外（必ず中継）。
        consoleLog(level, msg, tag) {
            const t = (tag === undefined ? null : tag);
            const now = Date.now();
            if (t !== null && !TAG_DEDUP_EXEMPT.has(t)) {
                const last = _tagLastRelay.get(t) || 0;
                if (now - last < TAG_DEDUP_MS) {
                    _tagSuppressed.set(t, (_tagSuppressed.get(t) || 0) + 1);
                    scheduleSuppressedFlush(t);
                    return;
                }
                _tagLastRelay.set(t, now);
                const suppressed = _tagSuppressed.get(t) || 0;
                if (suppressed > 0) {
                    _tagSuppressed.set(t, 0);
                    relay({ kind: 'console', level: 'info', tag: t, msg: '[suppressed] ' + t + ' x' + suppressed, route: currentRoute() });
                }
            }
            relay({ kind: 'console', level: level, tag: t, msg: String(msg).slice(0, 500), route: currentRoute() });
        }
    };
})();
