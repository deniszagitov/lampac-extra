/* Optional EX.TvCtl receiver for MCP. Pair this browser profile before loading the plugin. */
(function () {
    'use strict';

    const token = localStorage.getItem('tvctl_mcp_device_token');
    if (!token || !window.isSecureContext || window.tvctlMcpStarted) return;
    const script = document.currentScript;
    const base = script ? new URL(script.src, location.href).origin : location.origin;
    if (base !== location.origin) return;
    window.tvctlMcpStarted = true;

    const session = crypto.randomUUID().replace(/-/g, '');
    const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
    const title = card => card.title || card.name || card.original_title || card.original_name;
    const year = card => Number((card.release_date || card.first_air_date || '').slice(0, 4));
    const normalized = value => String(value || '').normalize('NFKC').trim().toLocaleLowerCase();
    const active = () => Lampa.Activity.active() || {};
    const video = () => document.querySelector('video');
    let selection;

    function busy() {
        const player = video();
        return player && !player.paused && !player.ended && player.currentSrc;
    }

    function cardVisible(card) {
        const view = active();
        if (document.visibilityState !== 'visible' || view.component !== 'full' ||
            Number((view.card || {}).id || view.id) !== card.id) return false;
        const container = view.activity && view.activity.render();
        const root = container && (container[0] || container);
        if (!root) return false;
        return Array.from(root.querySelectorAll('.full-start-new__title,.full-start__title')).some(el =>
            el.getClientRects().length && normalized(el.textContent).includes(normalized(title(card))));
    }

    async function present(id, args) {
        selection = null;
        if (busy()) return {state: 'screen_busy'};
        const kind = args.kind === 'series' ? 'tv' : 'movie';
        const data = await Promise.race([
            new Promise(done => Lampa.Api.search({query: encodeURIComponent(args.query)}, done)),
            sleep(12000).then(() => { throw Error('search_timeout'); })
        ]);
        let cards = ((data[kind] || {}).results || []).filter(c => !args.year || year(c) === args.year);
        const exact = cards.filter(c => [title(c), c.original_name, c.original_title].some(t =>
            normalized(t) === normalized(args.query)));
        if (exact.length) cards = exact;
        if (!cards.length) return {state: 'not_found'};
        if (cards.length > 1) return {state: 'choices', choices: cards.slice(0, 8).map(c => ({title: title(c), year: year(c)}))};
        if (busy()) return {state: 'screen_busy'};

        const card = cards[0];
        Lampa.Activity.push({url: '', component: 'full', method: kind, id: card.id,
            card: Object.assign({}, card, {source: 'tmdb'}), source: 'tmdb'});
        for (let i = 0; i < 60 && !cardVisible(card); i++) await sleep(200);
        if (!cardVisible(card)) return {state: 'card_unconfirmed'};
        selection = {id, card, args, view: active()};
        return {state: 'shown', title: title(card), year: year(card), tmdb_id: card.id};
    }

    async function lampac(path, params) {
        const url = new URL(path, base);
        if (url.origin !== base) throw Error('unsupported_source');
        for (const [key, value] of Object.entries(params)) url.searchParams.set(key, value);
        const response = await fetch(url, {signal: AbortSignal.timeout(10000)});
        if (!response.ok) throw Error('source_unavailable');
        return response.json();
    }

    // Follow the Online module's own provider and season links; signed URLs stay in the player.
    async function resolve(card, args) {
        const serial = args.kind === 'series' ? 1 : 0;
        const ids = await lampac('/externalids', {id: card.id, serial});
        const params = {id: card.id, tmdb_id: card.id, serial, source: 'tmdb', title: title(card),
            original_title: card.original_title || card.original_name || title(card), year: year(card)};
        if (ids.imdb_id) params.imdb_id = ids.imdb_id;
        if (ids.kinopoisk_id) params.kinopoisk_id = ids.kinopoisk_id;
        const events = await lampac('/lite/events', params);
        const sources = Array.isArray(events) ? events.filter(s => s.show && s.balanser && s.url) : [];
        if (!sources.length) return {state: 'source_unavailable'};
        const source = args.provider ? sources.find(s => normalized(s.balanser) === normalized(args.provider)) :
            (sources.length === 1 ? sources[0] : null);
        if (!source) return {state: 'choose_provider', providers: sources.map(s => s.balanser)};

        let list = await lampac(source.url, {rjson: 'true'});
        if (serial && list.type === 'season') {
            const season = (list.data || []).find(row => Number(row.id) === args.season && row.url);
            if (!season) return {state: 'episode_unavailable'};
            list = await lampac(season.url, {rjson: 'true'});
        }
        const rows = Array.isArray(list.data) ? list.data : [];
        const row = rows.find(r => r.stream && (!serial || (Number(r.s) === args.season && Number(r.e) === args.episode)));
        if (!row) return {state: 'no_playable_stream'};
        const stream = new URL(row.stream, base);
        if (!['http:', 'https:'].includes(stream.protocol)) return {state: 'unsupported_source'};
        return {stream: stream.href, provider: source.balanser};
    }

    async function play(id) {
        const chosen = selection;
        const valid = () => chosen && chosen.id === id && active() === chosen.view && cardVisible(chosen.card) && !busy();
        if (!valid()) return {state: 'stale_selection'};
        const {card, args} = chosen;
        if (args.kind === 'series' && args.episode == null) return {state: 'need_episode'};
        const source = await resolve(card, args);
        if (!source.stream) return source;
        if (!valid()) return {state: 'stale_selection'};
        Lampa.Player.play({title: title(card), url: source.stream});
        let last;
        for (let i = 0; i < 60; i++) {
            await sleep(250);
            const player = video();
            if (!player || !player.videoWidth || player.paused) continue;
            const frames = player.getVideoPlaybackQuality ? player.getVideoPlaybackQuality().totalVideoFrames : player.webkitDecodedFrameCount;
            if (last && player === last.player && frames > last.frames && player.currentTime > last.time)
                return {state: 'playing', tmdb_id: card.id, season: args.season, episode: args.episode, provider: source.provider};
            last = {player, frames, time: player.currentTime};
        }
        return {state: 'playback_unconfirmed'};
    }

    async function poll() {
        let report = {};
        for (;;) {
            try {
                const response = await fetch('/tvctl/mcp/device', {method: 'POST', cache: 'no-store',
                    headers: {Authorization: 'Bearer ' + token, 'Content-Type': 'application/json'},
                    body: JSON.stringify(Object.assign({session}, report)), signal: AbortSignal.timeout(8000)});
                if (response.status === 401 || response.status === 403) return;
                if (!response.ok) throw Error('device_offline');
                report = {};
                if (response.status !== 204) {
                    const job = await response.json();
                    let result;
                    try {
                        result = job.action === 'present' ? await present(job.id, job.args) : await play(job.args.selection_id);
                    } catch (e) {
                        result = {state: ['search_timeout', 'source_unavailable', 'unsupported_source'].includes(e.message)
                            ? e.message : 'operation_failed'};
                    }
                    report = {id: job.id, result};
                    continue;
                }
            } catch (_) { await sleep(2000); }
            await sleep(1000);
        }
    }

    if (window.appready) poll();
    else Lampa.Listener.follow('app', e => { if (e.type === 'ready') poll(); });
})();
