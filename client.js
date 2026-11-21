window.__ModuleLoader__.load({
  id: 'dsh-lens-computer-control',
  factory(require) {
    const React = require('react')
    const h = React.createElement
    const PORT = 45871
    const BASE = 'http://127.0.0.1:' + PORT

    // 设计目标：像一块"设备状态卡"，而不是一个调试面板。
    // 三等分信息层次：品牌+状态 → 画面/动作 → 唯一的操作（急停）。
    //
    // 配色全部走宿主的设计令牌（--dsw-alias-*），拿不到时回退到深色值 ——
    // 这样浅色主题下卡片不会变成"深色块贴在白底上"。
    const CSS = `
.lensHud{
  --lens-fg:var(--dsw-alias-label-primary,#e6eefc);
  --lens-fg2:var(--dsw-alias-label-secondary,#9aa7b8);
  --lens-fg3:var(--dsw-alias-label-tertiary,#6b7686);
  --lens-bg:var(--dsw-alias-bg-layer-1,#0f141c);
  --lens-bg2:var(--dsw-alias-bg-layer-2,#151b25);
  --lens-line:var(--dsw-alias-border-l2,rgba(255,255,255,.10));
  --lens-accent:#38bdf8;
  --lens-ok:#34d399;
  --lens-warn:#fbbf24;
  --lens-bad:#f87171;
  --lens-green:#22c55e;
  position:fixed;right:20px;bottom:20px;width:max-content;z-index:2147483000;
  font-family:-apple-system,BlinkMacSystemFont,"Segoe UI","Microsoft YaHei UI",system-ui,sans-serif;
  color:var(--lens-fg);-webkit-font-smoothing:antialiased;user-select:none;
  font-size:12px}
.lensHud *{box-sizing:border-box}

.lensCard{position:relative;width:340px;max-width:calc(100vw - 24px);border-radius:16px;overflow:hidden;
  background:linear-gradient(180deg,
    color-mix(in srgb,var(--lens-bg) 92%,transparent),
    color-mix(in srgb,var(--lens-bg) 97%,transparent));
  box-shadow:0 22px 54px -14px rgba(0,0,0,.55),0 0 0 1px color-mix(in srgb,var(--lens-line) 70%,transparent);
  backdrop-filter:blur(20px) saturate(150%);-webkit-backdrop-filter:blur(20px) saturate(150%);
  transition:width .18s cubic-bezier(.4,0,.2,1),box-shadow .18s ease}
/* 渐变描边：比纯色边框更有层次 */
.lensCard::before{content:'';position:absolute;inset:0;border-radius:inherit;padding:1px;pointer-events:none;
  background:linear-gradient(145deg,rgba(56,189,248,.45),rgba(56,189,248,.04) 40%,rgba(255,255,255,.04) 66%,rgba(129,140,248,.38));
  -webkit-mask:linear-gradient(#000 0 0) content-box,linear-gradient(#000 0 0);
  -webkit-mask-composite:xor;mask-composite:exclude}
.lensHud.isStopped .lensCard::before{background:linear-gradient(145deg,rgba(248,113,113,.6),rgba(248,113,113,.08) 45%,rgba(248,113,113,.4))}
.lensHud.isOffline .lensCard::before{background:linear-gradient(145deg,rgba(148,163,184,.22),rgba(148,163,184,.04))}
.lensHud.isOffline{opacity:.9}

/* ── 头部 ── */
.lensHead{display:flex;align-items:center;gap:9px;padding:10px 12px 10px 13px;cursor:grab;touch-action:none}
.lensHead:active{cursor:grabbing}
.lensBrand{display:flex;align-items:center;gap:7px;min-width:0}
.lensDot{width:7px;height:7px;border-radius:50%;background:var(--lens-ok);position:relative;flex:0 0 auto}
.lensDot::after{content:'';position:absolute;inset:-4px;border-radius:50%;background:rgba(52,211,153,.32);animation:lensPing 2.4s cubic-bezier(0,0,.2,1) infinite}
@keyframes lensPing{0%{transform:scale(.6);opacity:.8}80%,100%{transform:scale(2.1);opacity:0}}
.lensDot.off{background:var(--lens-bad)}
.lensDot.off::after{background:rgba(248,113,113,.4);animation:none}
.lensDot.idle{background:#64748b}
.lensDot.idle::after{display:none}

.lensName{font-size:12px;font-weight:700;letter-spacing:.14em;
  background:linear-gradient(90deg,var(--lens-fg),color-mix(in srgb,var(--lens-accent) 62%,var(--lens-fg)));
  -webkit-background-clip:text;background-clip:text;color:transparent}
.lensState{margin-left:auto;font-size:9.5px;font-weight:700;letter-spacing:.1em;
  padding:3px 8px;border-radius:999px;font-variant-numeric:tabular-nums;white-space:nowrap;
  color:color-mix(in srgb,var(--lens-accent) 72%,var(--lens-fg));
  background:color-mix(in srgb,var(--lens-accent) 13%,transparent);
  border:1px solid color-mix(in srgb,var(--lens-accent) 26%,transparent)}
.lensHud.isStopped .lensState{color:#fca5a5;background:rgba(248,113,113,.12);border-color:rgba(248,113,113,.28)}
.lensHud.isOffline .lensState{color:var(--lens-fg3);background:rgba(148,163,184,.1);border-color:rgba(148,163,184,.18)}
.lensGrip{display:flex;flex-direction:column;gap:2px;margin-left:2px;opacity:.4;flex:0 0 auto}
.lensGrip i{display:block;width:2px;height:2px;border-radius:50%;background:currentColor;box-shadow:5px 0 0 currentColor,-5px 0 0 currentColor}
.lensChev{color:var(--lens-fg3);display:flex;transition:transform .18s ease;flex:0 0 auto}
.lensHud.isCollapsed .lensChev{transform:rotate(-90deg)}

/* ── 画面 ── */
.lensBody{border-top:1px solid var(--lens-line);cursor:grab}
.lensShot{position:relative;background:var(--lens-bg2);overflow:hidden}
.lensShot img{display:block;width:100%;height:138px;object-fit:cover;object-position:top center;opacity:.97}
.lensShotBar{position:absolute;left:0;right:0;bottom:0;display:flex;align-items:center;gap:7px;
  padding:20px 12px 9px;font-size:11px;
  background:linear-gradient(180deg,rgba(6,9,14,0),rgba(6,9,14,.9))}
.lensWin{color:#e4eefc;font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:186px}
.lensChip{font-size:9.5px;padding:2px 7px;border-radius:6px;color:#7dd3fc;
  background:rgba(56,189,248,.13);border:1px solid rgba(56,189,248,.18);font-variant-numeric:tabular-nums;white-space:nowrap}
.lensChip.muted{color:var(--lens-fg3);background:rgba(148,163,184,.1);border-color:rgba(148,163,184,.16)}

/* ── 空状态 ── */
.lensVoid{display:flex;flex-direction:column;align-items:center;gap:10px;padding:22px 18px 20px;text-align:center}
.lensRing{width:36px;height:36px;border-radius:50%;border:1px dashed rgba(125,211,252,.3);
  display:flex;align-items:center;justify-content:center;position:relative}
.lensRing::before{content:'';width:9px;height:9px;border-radius:50%;background:rgba(125,211,252,.45);
  box-shadow:0 0 12px rgba(56,189,248,.55);animation:lensBreath 2.6s ease-in-out infinite}
@keyframes lensBreath{0%,100%{opacity:.45;transform:scale(.85)}50%{opacity:1;transform:scale(1.1)}}
.lensVoidText{font-size:11.5px;color:var(--lens-fg2);line-height:1.55}
.lensVoidText b{color:var(--lens-fg);font-weight:600}

/* ── 动作流 ── */
.lensFeedHead{display:flex;align-items:center;justify-content:space-between;
  padding:10px 13px 4px;font-size:9.5px;letter-spacing:.12em;color:var(--lens-fg3);font-weight:700}
.lensRows{cursor:default;padding:0 5px 6px;max-height:124px;overflow:auto;scrollbar-width:thin;scrollbar-color:rgba(148,163,184,.22) transparent}
.lensRows::-webkit-scrollbar{width:6px}
.lensRows::-webkit-scrollbar-thumb{background:rgba(148,163,184,.2);border-radius:3px}
.lensRow{display:grid;grid-template-columns:44px 1fr 8px;gap:8px;align-items:center;
  padding:5px 7px;border-radius:8px;transition:background .13s ease}
.lensRow:hover{background:color-mix(in srgb,var(--lens-accent) 7%,transparent)}
.lensTime{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:9px;
  color:var(--lens-fg3);font-variant-numeric:tabular-nums}
.lensAct{display:flex;align-items:baseline;gap:6px;min-width:0}
.lensVerb{font-size:9px;font-weight:700;letter-spacing:.05em;flex:0 0 auto;text-transform:uppercase;
  padding:2px 5px;border-radius:5px;
  color:color-mix(in srgb,var(--lens-accent) 72%,var(--lens-fg));
  background:color-mix(in srgb,var(--lens-accent) 12%,transparent)}
.lensVerb.k-click,.lensVerb.k-run{color:color-mix(in srgb,#a78bfa 70%,var(--lens-fg));background:color-mix(in srgb,#a78bfa 14%,transparent)}
.lensVerb.k-scan,.lensVerb.k-check{color:color-mix(in srgb,var(--lens-accent) 72%,var(--lens-fg));background:color-mix(in srgb,var(--lens-accent) 12%,transparent)}
.lensVerb.k-blocked,.lensVerb.k-cancel{color:color-mix(in srgb,#f87171 72%,var(--lens-fg));background:color-mix(in srgb,#f87171 14%,transparent)}
.lensDet{font-size:11px;color:var(--lens-fg2);overflow:hidden;text-overflow:ellipsis;white-space:nowrap;min-width:0}
.lensOk{width:6px;height:6px;border-radius:50%;background:var(--lens-ok);box-shadow:0 0 8px rgba(52,211,153,.5)}
.lensOk.bad{background:var(--lens-warn);box-shadow:0 0 8px rgba(251,191,36,.5)}

/* ── 底部 ── */
.lensFoot{display:flex;align-items:center;gap:10px;padding:9px 11px 9px 13px;
  border-top:1px solid var(--lens-line);background:color-mix(in srgb,var(--lens-bg2) 55%,transparent)}
.lensHint{font-size:10.5px;color:var(--lens-fg3);letter-spacing:.02em;flex:1 1 auto;
  overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.lensHint b{color:var(--lens-fg2);font-weight:600}
.lensStop{border:1px solid rgba(248,113,113,.34);background:rgba(248,113,113,.09);
  color:color-mix(in srgb,#f87171 68%,var(--lens-fg));border-radius:9px;padding:7px 14px;font:600 11.5px/1 inherit;cursor:pointer;
  letter-spacing:.06em;transition:all .14s ease;flex:0 0 auto}
.lensStop:hover:not(:disabled){background:rgba(248,113,113,.18);border-color:rgba(248,113,113,.6);
  box-shadow:0 0 18px -4px rgba(248,113,113,.45)}
.lensStop:active:not(:disabled){transform:translateY(1px)}
.lensStop:disabled{opacity:.45;cursor:default}
.lensStop.armed{border-color:rgba(52,211,153,.36);background:rgba(52,211,153,.1);color:color-mix(in srgb,#34d399 62%,var(--lens-fg))}
.lensStop.armed:hover:not(:disabled){background:rgba(52,211,153,.18);border-color:rgba(52,211,153,.6);
  box-shadow:0 0 18px -4px rgba(52,211,153,.45)}

/* 折叠态：宽度交给内容决定，不要留固定宽 —— 否则右边会多出一块既挡页面又拖不动的透明区 */
.lensHud.isCollapsed .lensCard{width:auto;max-width:min(420px,calc(100vw - 24px))}
.lensMin{display:flex;align-items:center;gap:8px;padding-right:2px;max-width:260px}
.lensMinText{font-size:11px;color:var(--lens-fg2);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}

/* 待确认的坐标点击：绿框图 + 确认/取消 */
.lensPending{border-top:1px solid rgba(34,197,94,.22);background:rgba(34,197,94,.07);padding:10px 12px 11px}
.lensPendingTop{display:flex;align-items:center;justify-content:space-between;gap:8px;font-size:11.5px;color:color-mix(in srgb,#22c55e 58%,var(--lens-fg))}
.lensPendingTop b{font-weight:700;color:color-mix(in srgb,#22c55e 26%,var(--lens-fg));font-variant-numeric:tabular-nums}
.lensPendMeta{display:flex;align-items:center;gap:6px;margin-top:6px;font-size:10.5px;color:var(--lens-fg2)}
.lensPendMeta .lensChip{color:color-mix(in srgb,#22c55e 55%,var(--lens-fg));
  background:color-mix(in srgb,#22c55e 13%,transparent);border-color:color-mix(in srgb,#22c55e 26%,transparent)}
.lensPending img{display:block;width:100%;margin:8px 0 9px;border-radius:9px;background:var(--lens-bg2);
  box-shadow:0 0 0 1px rgba(34,197,94,.32)}
.lensPendingBtns{display:flex;gap:8px}
.lensPendingBtns button{flex:1;padding:8px 0;border:0;border-radius:9px;cursor:pointer;font-size:11.5px;font-family:inherit;
  transition:filter .14s ease}
.lensPendingBtns button:disabled{opacity:.5;cursor:default}
.lensPendingBtns button:hover:not(:disabled){filter:brightness(1.08)}
.lensPendingBtns .ok{background:var(--lens-green);color:#04150b;font-weight:700}
.lensPendingBtns .no{background:color-mix(in srgb,var(--lens-fg) 10%,transparent);color:var(--lens-fg)}
`

    function useStatus() {
      const [state, setState] = React.useState({ loading: true })
      React.useEffect(() => {
        let alive = true
        const tick = () => {
          fetch(BASE + '/status')
            .then(function (r) { return r.json() })
            .then(function (j) { if (alive) setState(j) })
            .catch(function () { if (alive) setState({ offline: true }) })
        }
        tick()
        const timer = setInterval(tick, 1200)
        return function () {
          alive = false
          clearInterval(timer)
        }
      }, [])
      return [state, setState]
    }

    // ── 位置记忆与拖动 ──
    const POS_KEY = 'lens-hud-pos'

    function loadPos() {
      try {
        const raw = window.localStorage.getItem(POS_KEY)
        if (!raw) return null
        const p = JSON.parse(raw)
        if (typeof p.x === 'number' && typeof p.y === 'number') return p
      } catch (e) {}
      return null
    }

    function savePos(p) {
      try {
        if (p) window.localStorage.setItem(POS_KEY, JSON.stringify(p))
        else window.localStorage.removeItem(POS_KEY)
      } catch (e) {}
    }

    // 夹在视口内，别让面板被拖到看不见的地方
    function clampPos(p, w, h) {
      const pad = 6
      const maxX = Math.max(pad, window.innerWidth - w - pad)
      const maxY = Math.max(pad, window.innerHeight - h - pad)
      return { x: Math.min(Math.max(pad, p.x), maxX), y: Math.min(Math.max(pad, p.y), maxY) }
    }
    function clock(ms) {
      const d = new Date(ms)
      const p = function (n) { return String(n).padStart(2, '0') }
      return p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds())
    }

    // 细箭头，纯 CSS 画，避免引外部字体
    function Chevron() {
      return h('svg', { className: 'lensChev', width: 14, height: 14, viewBox: '0 0 24 24', fill: 'none' },
        h('path', { d: 'M6 9l6 6 6-6', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round', strokeLinejoin: 'round' }))
    }

    /** HUD 里的「待确认点击」卡片：绿框预览图 + 确认 / 取消。 */
    function PendingCard(props) {
      const item = props.item || {}
      const onDecide = props.onDecide || function () {}
      const cand = item.candidate
      return h('div', { className: 'lensPending' },
        h('div', { className: 'lensPendingTop' },
          h('span', null, '待确认点击 ', h('b', null, '(' + item.x + ', ' + item.y + ')')),
          props.more > 0 ? h('span', { className: 'lensChip muted' }, '还有 ' + props.more + ' 条') : null),
        (cand || item.covered)
          ? h('div', { className: 'lensPendMeta' },
              cand ? h('span', { className: 'lensChip' }, '#' + cand.id + ' ' + (cand.text || '')) : null,
              item.covered ? h('span', { className: 'lensChip muted' }, '被遮挡') : null)
          : null,
        h('img', {
          src: BASE + '/preview?token=' + encodeURIComponent(item.token) + '&t=' + item.at,
          alt: '点击预览',
        }),
        h('div', { className: 'lensPendingBtns' },
          h('button', {
            className: 'ok',
            disabled: props.busy,
            onClick: function (e) { e.stopPropagation(); onDecide(item.token, 'confirm') },
          }, '确认点击'),
          h('button', {
            className: 'no',
            disabled: props.busy,
            onClick: function (e) { e.stopPropagation(); onDecide(item.token, 'cancel') },
          }, '取消')))
    }

    function Hud() {
      const [state, setState] = useStatus()
      const [collapsed, setCollapsed] = React.useState(false)
      const [busy, setBusy] = React.useState(false)
      const [pos, setPos] = React.useState(loadPos)

      const rootRef = React.useRef(null)
      const dragRef = React.useRef(null)
      const movedRef = React.useRef(false)

      // 位置持久化
      React.useEffect(function () { savePos(pos) }, [pos])

      // 每次渲染后把面板拉回可见范围。
      // 只靠拖动/resize 夹边界不够：初始加载（不同窗口尺寸的旧位置）、
      // 折叠/展开（宽度变了）、动作流加长（高度变了）都可能让它跑出窗口。
      React.useEffect(function () {
        if (!pos) return
        const el = rootRef.current
        if (!el) return
        const r = el.getBoundingClientRect()
        const c = clampPos(pos, r.width, r.height)
        if (Math.abs(c.x - pos.x) > 0.5 || Math.abs(c.y - pos.y) > 0.5) setPos(c)
      })

      React.useEffect(function () {
        function onResize() {
          const el = rootRef.current
          if (!el || !pos) return
          const r = el.getBoundingClientRect()
          const c = clampPos(pos, r.width, r.height)
          if (c.x !== pos.x || c.y !== pos.y) setPos(c)
        }
        window.addEventListener('resize', onResize)
        return function () { window.removeEventListener('resize', onResize) }
      }, [pos])

      // 拖动：mousedown 记起点，window 上跟手，松手结束
      React.useEffect(function () {
        function onMove(e) {
          const d = dragRef.current
          if (!d) return
          const dx = e.clientX - d.sx
          const dy = e.clientY - d.sy
          if (!d.moved && Math.abs(dx) + Math.abs(dy) < 4) return   // 4px 死区：区分点击与拖动
          d.moved = true
          movedRef.current = true
          // 每次移动都重新量尺寸：折叠/展开会改高度，用按下瞬间的旧值会把边界算错
          const el = rootRef.current
          const r = el ? el.getBoundingClientRect() : { width: d.w, height: d.h }
          setPos(clampPos({ x: d.ox + dx, y: d.oy + dy }, r.width, r.height))
        }
        function onUp() {
          if (dragRef.current && dragRef.current.moved) {
            // 拖动结束后短暂抑制 click，避免误触折叠
            window.setTimeout(function () { movedRef.current = false }, 0)
          }
          dragRef.current = null
        }
        window.addEventListener('mousemove', onMove)
        window.addEventListener('mouseup', onUp)
        return function () {
          window.removeEventListener('mousemove', onMove)
          window.removeEventListener('mouseup', onUp)
        }
      }, [])

      const startDrag = function (e) {
        if (e.button !== 0) return
        const el = rootRef.current
        if (!el) return
        const r = el.getBoundingClientRect()
        dragRef.current = { sx: e.clientX, sy: e.clientY, ox: r.left, oy: r.top, w: r.width, h: r.height, moved: false }
        e.preventDefault()
      }

      const toggleCollapse = function () {
        if (movedRef.current) return          // 刚才是在拖，不是点
        setCollapsed(!collapsed)
      }

      // 双击头部复位到默认位置（右下角）
      const resetPos = function (e) {
        e.stopPropagation()
        setPos(null)
      }


      const offline = state.offline === true
      const stopped = state.stopped === true
      const rows = state.timeline || []
      const last = state.lastScan

      const hit = function (path) {
        setBusy(true)
        fetch(BASE + path, { method: 'POST' })
          .then(function (r) { return r.json() })
          .then(function (j) { setState(function (s) { return Object.assign({}, s, { stopped: j.stopped }) }) })
          .catch(function () {})
          .finally(function () { setBusy(false) })
      }

      // HUD 上的人工裁决：确认 = 真的点下去，取消 = 丢掉这次预览
      const decide = function (token, action) {
        setBusy(true)
        fetch(BASE + '/' + action + '?token=' + encodeURIComponent(token), { method: 'POST' })
          .then(function (r) { return r.json() })
          .then(function (j) {
            setState(function (s) {
              const rest = (s.pending || []).filter(function (p) { return p.token !== token })
              return Object.assign({}, s, { pending: rest, lastDecision: { token: token, action: action, result: j } })
            })
          })
          .catch(function () {})
          .finally(function () { setBusy(false) })
      }

      const dotClass = 'lensDot' + (offline ? ' idle' : stopped ? ' off' : '')
      const stateText = offline ? 'OFFLINE' : stopped ? 'STOPPED' : (last && last.level ? last.level : 'READY')

      const rootClass = 'lensHud' + (collapsed ? ' isCollapsed' : '') +
        (stopped ? ' isStopped' : '') + (offline ? ' isOffline' : '')

      // 拖动后的位置用 left/top 覆盖默认的 right/bottom
      const posStyle = pos ? { left: pos.x + 'px', top: pos.y + 'px', right: 'auto', bottom: 'auto' } : null

      // 折叠成一条时也留一句"刚才干了什么"——否则缩起来完全没有信息量
      const brief = collapsed && rows[0]
        ? h('span', { className: 'lensMinText', title: rows[0].detail },
            rows[0].detail || String(rows[0].kind || ''))
        : null

      const head = h('div', {
        className: 'lensHead',
        onMouseDown: startDrag,
        onClick: toggleCollapse,
        onDoubleClick: resetPos,
        title: '拖动可移动 · 单击折叠 · 双击回到右下角',
      },
        h('span', { className: 'lensBrand' },
          h('span', { className: dotClass }),
          h('span', { className: 'lensName' }, 'LENS')),
        brief,
        h('span', { className: 'lensState' }, stateText),
        h('span', { className: 'lensGrip', 'aria-hidden': 'true' }, h('i')),
        h(Chevron))

      if (collapsed) {
        return h('div', { className: rootClass, ref: rootRef, style: posStyle },
          h('style', null, CSS),
          h('div', { className: 'lensCard' }, head))
      }

      // ── 画面区：有截图显示截图，否则是一个安静的待命态 ──
      const shot = last && last.overlay
        ? h('div', { className: 'lensShot' },
            h('img', { src: BASE + '/thumb?t=' + last.at, alt: last.window || 'scan' }),
            h('div', { className: 'lensShotBar' },
              h('span', { className: 'lensWin', title: last.window }, last.window || '未命名窗口'),
              last.candidates != null
                ? h('span', { className: 'lensChip' }, last.candidates + ' 候选')
                : null))
        : h('div', { className: 'lensVoid' },
            h('span', { className: 'lensRing' }),
            h('div', { className: 'lensVoidText' },
              offline
                ? ['Lens 引擎未连上', h('br'), '检查插件 Host 半是否启动']
                : [h('b', null, '待命中'), ' · 还没有扫描过窗口']))

      // ── 待确认的坐标点击：模型点了坐标但还没落点，等这里的人确认 ──
      const pending = state.pending || []
      const pendingCard = pending.length
        ? h(PendingCard, { item: pending[0], more: pending.length - 1, busy: busy, onDecide: decide })
        : null

      const feed = rows.length
        ? h('div', null,
            h('div', { className: 'lensFeedHead' },
              h('span', null, '最近动作'),
              h('span', null, rows.length + '')),
            h('div', { className: 'lensRows' },
              rows.slice(0, 20).map(function (r, i) {
                return h('div', { className: 'lensRow', key: i },
                  h('span', { className: 'lensTime' }, clock(r.at)),
                  h('span', { className: 'lensAct' },
                    h('span', { className: 'lensVerb k-' + String(r.kind || '').toLowerCase() },
                      String(r.kind || '').toUpperCase()),
                    h('span', { className: 'lensDet', title: r.detail }, r.detail || '')),
                  h('span', { className: 'lensOk' + (r.ok ? '' : ' bad') }))
              })))
        : null

      const foot = h('div', { className: 'lensFoot' },
        h('span', { className: 'lensHint' },
          offline ? '引擎离线' : stopped ? '已拦截所有 computer_* 调用' : 'AI 随时可以接管桌面'),
        h('button', {
          className: 'lensStop' + (stopped ? ' armed' : ''),
          disabled: busy || offline,
          onClick: function (e) { e.stopPropagation(); hit(stopped ? '/resume' : '/stop') },
        }, stopped ? '解除' : '急停'))

      return h('div', { className: rootClass, ref: rootRef, style: posStyle },
        h('style', null, CSS),
        h('div', { className: 'lensCard' },
          head,
          h('div', { className: 'lensBody', onMouseDown: startDrag }, shot, pendingCard, feed),
          foot))
    }

    // ------------------------------------------------------------------ 设置页
    // 设置 → Lens 电脑控制。宿主用 configForms 把这个插件行（id = lens-computer-control）
    // 的配置暴露成一个 scope；这里只负责把 scope 画成表单并把改动写回去。
    const SETTINGS_ID = 'lens-computer-control'

    const DEFAULTS = {
      hudEnabled: true,
      humanize: true,
      confirmClicks: true,
      autoCheck: false,
      timeoutMs: 25000,
      checkBox: 24,
      checkZoom: 8,
      blockedTitles: [],
      hudPort: 45871,
    }

    const SET_CSS = `
.lensSet{display:flex;flex-direction:column;gap:18px;font-size:13px;color:var(--dsw-alias-label-primary,#e6eefc)}
.lensSet h2{font-size:18px;line-height:1.4;font-weight:600;margin:0}
.lensSet .lensRow{display:flex;align-items:flex-start;gap:16px;padding:2px 0}
.lensSet .lensRowText{flex:1;min-width:0}
.lensSet .lensRowTitle{font-size:13.5px;line-height:1.5;font-weight:500}
.lensSet .lensRowHint{font-size:12px;line-height:1.6;color:var(--dsw-alias-label-tertiary,#8b94a7);margin-top:2px}
.lensSet .lensCtrl{flex:none;display:flex;align-items:center;gap:8px}
.lensSet input[type=number]{width:104px;padding:6px 8px;border-radius:8px;border:.5px solid var(--dsw-alias-border-l2,rgba(255,255,255,.18));background:var(--dsw-alias-bg-layer-2,rgba(255,255,255,.04));color:inherit;font:inherit;text-align:right}
.lensSet textarea{width:100%;min-height:64px;padding:8px 10px;border-radius:10px;border:.5px solid var(--dsw-alias-border-l2,rgba(255,255,255,.18));background:var(--dsw-alias-bg-layer-2,rgba(255,255,255,.04));color:inherit;font:inherit;resize:vertical}
.lensSet .lensSwitch{position:relative;width:42px;height:24px;border-radius:999px;border:0;cursor:pointer;background:var(--dsw-alias-bg-layer-3,rgba(255,255,255,.16));transition:background .16s}
.lensSet .lensSwitch[data-on=true]{background:#2f7cf6}
.lensSet .lensSwitch:after{content:'';position:absolute;top:3px;left:3px;width:18px;height:18px;border-radius:50%;background:#fff;transition:transform .16s}
.lensSet .lensSwitch[data-on=true]:after{transform:translateX(18px)}
.lensSet .lensSwitch:disabled{opacity:.45;cursor:default}
.lensSet .lensActions{display:flex;align-items:center;gap:10px;padding-top:4px}
.lensSet button.lensSave{padding:7px 16px;border-radius:9px;border:0;cursor:pointer;background:#2f7cf6;color:#fff;font:inherit;font-weight:600}
.lensSet button.lensSave:disabled{opacity:.45;cursor:default}
.lensSet button.lensGhost{padding:7px 14px;border-radius:9px;cursor:pointer;background:none;color:inherit;border:.5px solid var(--dsw-alias-border-l2,rgba(255,255,255,.18));font:inherit}
.lensSet .lensMsg{font-size:12.5px;color:var(--dsw-alias-label-tertiary,#8b94a7)}
.lensSet .lensMsg[data-tone=err]{color:#f0655d}
.lensSet .lensMsg[data-tone=ok]{color:#3ecf8e}
.lensSet .lensSec{font-size:12px;font-weight:600;letter-spacing:.04em;text-transform:uppercase;color:var(--dsw-alias-label-tertiary,#8b94a7);margin-top:6px}
`

    function asList(value) {
      if (Array.isArray(value)) return value
      return String(value == null ? '' : value)
        .split(/[,，\n]/)
        .map(function (s) { return s.trim() })
        .filter(Boolean)
    }

    function LensSwitch(props) {
      return h('button', {
        type: 'button',
        className: 'lensSwitch',
        'data-on': String(!!props.value),
        disabled: props.disabled,
        role: 'switch',
        'aria-checked': String(!!props.value),
        'aria-label': props.label,
        onClick: function () { props.onChange(!props.value) },
      })
    }

    function LensRow(props) {
      return h('div', { className: 'lensRow' },
        h('div', { className: 'lensRowText' },
          h('div', { className: 'lensRowTitle' }, props.title),
          props.hint ? h('div', { className: 'lensRowHint' }, props.hint) : null),
        h('div', { className: 'lensCtrl' }, props.children))
    }

    function LensSettings(props) {
      // 先看宿主到底把哪些命名空间端出来了：插件行 id 有可能不是我们猜的那个，
      // 所以这里按「精确 id → 名字里含 lens」两级去找，找不到就把列表显示出来。
      const mirror = props.mirror
      const mirrorState = mirror
        ? React.useSyncExternalStore(
            function (cb) { return mirror.subscribe(cb) },
            function () { return mirror.getSnapshot() },
          )
        : null
      const namespaces = (mirrorState && mirrorState.view && mirrorState.view.namespaces) || []
      const nsNames = namespaces.map(function (n) { return n.ns })
      const exact = nsNames.indexOf(SETTINGS_ID) >= 0
      const guess = exact
        ? SETTINGS_ID
        : nsNames.filter(function (n) { return String(n).indexOf('lens') >= 0 })[0]
      const scope = props.pick ? props.pick(guess) : null

      const state = scope
        ? React.useSyncExternalStore(
            function (cb) { return scope.subscribe(cb) },
            function () { return scope.getSnapshot() },
          )
        : null
      const status = state && state.status
      const writable = !(state && state.writable === false)

      const saved = {}
      const value = (state && state.value) || {}
      Object.keys(DEFAULTS).forEach(function (k) {
        saved[k] = value[k] === undefined ? DEFAULTS[k] : value[k]
      })

      const [draft, setDraft] = React.useState(null)
      const [busy, setBusy] = React.useState(false)
      const [msg, setMsg] = React.useState('')
      const [tone, setTone] = React.useState('')

      const savedKey = JSON.stringify(saved)
      React.useEffect(function () { setDraft(null) }, [savedKey])

      const cur = draft || saved
      const dirty = draft !== null && JSON.stringify(cur) !== savedKey

      const set = function (field, next) {
        const base = draft || saved
        const merged = Object.assign({}, base)
        merged[field] = next
        setDraft(merged)
        setMsg('')
      }

      const save = async function () {
        if (!draft) return
        setBusy(true)
        setTone('')
        setMsg('保存中…')
        try {
          const patch = {}
          Object.keys(draft).forEach(function (k) {
            if (JSON.stringify(draft[k]) !== JSON.stringify(saved[k])) patch[k] = draft[k]
          })
          if (!Object.keys(patch).length) { setMsg('没有改动'); setTone('') }
          else {
            await makeSavePatch(scope)(patch)
            setMsg('已保存，下一次工具调用就按新设置跑')
            setTone('ok')
            setDraft(null)
          }
        } catch (e) {
          setMsg('保存失败：' + (e && e.message ? e.message : String(e)))
          setTone('err')
        } finally {
          setBusy(false)
        }
      }

      if (!scope) {
        return h('div', { className: 'lensSet' },
          h('style', null, SET_CSS),
          h('h2', null, 'Lens 电脑控制'),
          h('div', { className: 'lensMsg' }, '宿主还没有为这个插件提供配置项，所以暂时没有可改的开关。'),
          h('div', { className: 'lensActions' },
            h('button', { type: 'button', className: 'lensGhost', onClick: function () { window.location.reload() } }, '重新加载页面')),
          h('div', { className: 'lensRowHint' },
            '当前可编辑的命名空间：' + (nsNames.length ? nsNames.join('、') : '（无）')))
      }

      if (status && status !== 'ready') {
        return h('div', { className: 'lensSet' },
          h('style', null, SET_CSS),
          h('h2', null, 'Lens 电脑控制'),
          h('div', { className: 'lensMsg' }, status === 'loading' ? '设置加载中…' : '这些设置暂时没有加载好。'),
          h('div', { className: 'lensActions' },
            h('button', { type: 'button', className: 'lensGhost', onClick: function () { window.location.reload() } }, '重新加载页面')),
          h('div', { className: 'lensRowHint' }, '绑定的命名空间：' + (guess || '(未匹配)') + '（状态 ' + status + '）'))
      }

      return h('div', { className: 'lensSet' },
        h('style', null, SET_CSS),
        h('h2', null, 'Lens 电脑控制'),
        h('div', { className: 'lensRowHint' }, 'AI 操作桌面时的行为。改完点保存，下一次工具调用就生效（不用重启）。'),

        h('div', { className: 'lensSec' }, '行为'),
        h(LensRow, {
          title: '显示 HUD 浮层',
          hint: '屏幕上的渐变蓝边与状态胶囊，能看它正在点什么；关掉则完全静默。',
        }, h(LensSwitch, { value: !!cur.hudEnabled, disabled: !writable, label: '显示 HUD', onChange: function (v) { set('hudEnabled', v) } })),

        h(LensRow, {
          title: '人类化输入节奏',
          hint: '移动缓动 + 微抖动、按键间隔抖动。关掉后点击/输入/按键/拖动都用最快的瞬时动作。',
        }, h(LensSwitch, { value: !!cur.humanize, disabled: !writable, label: '人类化输入', onChange: function (v) { set('humanize', v) } })),

        h(LensRow, {
          title: '坐标点击先预览（不落点）',
          hint: '模型给 x/y 点击时不直接点：先跑坐标检验，回一张绿框图 + 确认令牌（ck_xxx），确认后才真正点击。关掉就是老行为。',
        }, h(LensSwitch, { value: !!cur.confirmClicks, disabled: !writable, label: '点击前预览', onChange: function (v) { set('confirmClicks', v) } })),

        h(LensRow, {
          title: '预览关闭时的兜底检验',
          hint: '只在上面「预览」关掉后生效：直接校验坐标，被遮挡或落在窗口外就拒绝点击。用候选 id / 网格点不受影响。',
        }, h(LensSwitch, { value: !!cur.autoCheck, disabled: !writable, label: '自动坐标检验', onChange: function (v) { set('autoCheck', v) } })),

        h('div', { className: 'lensSec' }, '引擎'),
        h(LensRow, {
          title: '单次调用超时',
          hint: '毫秒。大窗口 OCR 较慢时可调大，默认 25000。',
        }, h('input', {
          type: 'number', min: 2000, max: 120000, step: 1000, value: cur.timeoutMs, disabled: !writable,
          onChange: function (e) { set('timeoutMs', Number(e.target.value)) },
        }), h('span', null, 'ms')),

        h('div', { className: 'lensSec' }, '坐标检验'),
        h(LensRow, {
          title: '默认命中范围',
          hint: '绿框边长（屏幕像素），一般就是鼠标热区大小，默认 24。',
        }, h('input', {
          type: 'number', min: 8, max: 200, step: 2, value: cur.checkBox, disabled: !writable,
          onChange: function (e) { set('checkBox', Number(e.target.value)) },
        }), h('span', null, 'px')),

        h(LensRow, {
          title: '放大镜倍率',
          hint: '检验图右下角那块放大的倍数，2~16，默认 8。',
        }, h('input', {
          type: 'number', min: 2, max: 16, step: 1, value: cur.checkZoom, disabled: !writable,
          onChange: function (e) { set('checkZoom', Number(e.target.value)) },
        }), h('span', null, 'x')),

        h('div', { className: 'lensSec' }, '限制'),
        h(LensRow, { title: '永不操作的窗口', hint: '标题关键词，逗号或换行分隔（如：网银, 支付宝）。命中即拒绝动作并返回 WINDOW_BLOCKED。' },
          null),
        h('textarea', {
          value: asList(cur.blockedTitles).join(', '),
          disabled: !writable,
          placeholder: '网银, 支付宝, 公司财务系统',
          onChange: function (e) { set('blockedTitles', asList(e.target.value)) },
        }),

        h('div', { className: 'lensActions' },
          h('button', { type: 'button', className: 'lensSave', disabled: !writable || busy || !dirty, onClick: save }, busy ? '保存中…' : '保存'),
          h('button', { type: 'button', className: 'lensGhost', disabled: busy || !dirty, onClick: function () { setDraft(null); setMsg('') } }, '还原'),
          msg ? h('span', { className: 'lensMsg', 'data-tone': tone }, msg) : null),
        writable ? null : h('div', { className: 'lensMsg' }, '当前配置只读（可能由配置文件托管）。'))
    }

    /** 把 configForms 的 scope 包装成「字段 -> 值」的保存函数。 */
    function makeSavePatch(scope) {
      return async function savePatch(patch) {
        const snap = scope.getSnapshot()
        if (snap && snap.status !== undefined && snap.status !== 'ready') throw new Error('设置还没加载好')
        const ops = Object.keys(patch).map(function (field) {
          return { op: 'set', path: [field], value: patch[field] }
        })
        if (typeof scope.mutate === 'function') {
          const accepted = await scope.mutate(ops, snap && snap.revision)
          if (accepted === false) throw new Error('宿主拒绝了这次修改')
        } else if (typeof scope.set === 'function') {
          for (const field of Object.keys(patch)) await scope.set(field, patch[field])
        } else {
          throw new Error('这个宿主版本不支持直接改设置')
        }
        const after = scope.getSnapshot()
        const value = (after && after.value) || {}
        for (const field of Object.keys(patch)) {
          if (JSON.stringify(value[field]) !== JSON.stringify(patch[field])) throw new Error('改动没有落地')
        }
      }
    }

    return {
      inject: ['slots'],
      // 导出内部组件仅供本地测试直接渲染；宿主只读 inject / apply，多这一个键没有任何副作用
      internals: { PendingCard: PendingCard, LensSettings: LensSettings, Hud: Hud },
      apply(ctx) {
        ctx.slots.inject('shell.overlay', function () {
          return ctx.slots.register(
            { name: 'shell.overlay', id: 'lens-hud', order: 90, label: 'Lens HUD' },
            Hud,
          )
        })

        // 设置导航里的一项：设置 → Lens 电脑控制
        // 不在 mount 时死绑某个 scope —— 宿主提供的命名空间 id 可能不是我们猜的那个，
        // 也可能比这次 mount 更晚就绪。把 mirror 和取用器交给组件，由它每次渲染时按
        // 「精确 id → 含 lens」两级现挑。
        ctx.inject(['configForms'], function (owner) {
          let mirror = null
          try {
            mirror = owner.configForms.describe()
          } catch (e) {
            mirror = null
          }
          const pick = function (ns) {
            if (!ns) return null
            try {
              return owner.configForms.get(ns)
            } catch (e) {
              return null
            }
          }
          ctx.slots.inject('settings.section', function () {
            return ctx.slots.register(
              {
                name: 'settings.section',
                id: 'lens',
                order: 17,
                label: function () { return 'Lens 电脑控制' },
                inject: function () {
                  return { mirror: mirror, pick: pick, as: 'section', idPrefix: 'lens' }
                },
              },
              LensSettings,
            )
          })
          return function () {}
        })
      },
    }
  },
})
