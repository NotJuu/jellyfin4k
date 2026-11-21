/*
 * Anime4K for Jellyfin - web client integration
 *
 *  - Real-time Anime4K upscaling in the browser (WebGL2 implementation of the mpv user shader format)
 *  - Server side upscaling (for TVs): the Jellyfin server renders Anime4K with FFmpeg/libplacebo
 *  - Settings menu inside the video player, fully usable with a TV remote (D-pad, OK, Back, colour keys)
 *
 * Anime4K shaders: Copyright (c) 2019-2021 bloc97, MIT License, https://github.com/bloc97/Anime4K
 */
(function () {
    'use strict';

    if (window.__anime4kJellyfin) {
        return;
    }

    const SCRIPT_URL = (document.currentScript && document.currentScript.src) || '';
    const STORAGE_KEY = 'anime4k.settings.v1';
    const LOG_PREFIX = '[Anime4K]';

    const MODES = [
        { id: 'A', name: 'A', desc: 'Unscharfe Quellen (die meisten 1080p-Animes)' },
        { id: 'B', name: 'B', desc: 'Leicht unscharf, mehr Kompressionsartefakte' },
        { id: 'C', name: 'C', desc: 'Scharfe Quellen (z. B. 720p-Neuproduktionen)' },
        { id: 'AA', name: 'A+A', desc: 'Wie A, stärkere Rekonstruktion' },
        { id: 'BB', name: 'B+B', desc: 'Wie B, stärkere Rekonstruktion' },
        { id: 'CA', name: 'C+A', desc: 'Wie C, mit zusätzlicher Restaurierung' }
    ];
    const QUALITIES = [
        { id: 'Fast', name: 'Schnell', desc: 'Schwächere GPUs / Laptops' },
        { id: 'HQ', name: 'Hohe Qualität', desc: 'Mittelklasse- und High-End-GPUs' },
        { id: 'Ultra', name: 'Ultra', desc: 'Nur sehr starke GPUs' }
    ];
    const HEIGHTS = [1080, 1440, 2160];
    const LOCATIONS = [
        { id: 'off', name: 'Aus' },
        { id: 'client', name: 'Dieses Gerät (GPU)' },
        { id: 'server', name: 'Server (für TV)' }
    ];

    function log() {
        const args = Array.prototype.slice.call(arguments);
        args.unshift(LOG_PREFIX);
        console.log.apply(console, args);
    }

    function warn() {
        const args = Array.prototype.slice.call(arguments);
        args.unshift(LOG_PREFIX);
        console.warn.apply(console, args);
    }

    // ------------------------------------------------------------------------------------------
    // Settings
    // ------------------------------------------------------------------------------------------

    const defaultSettings = {
        location: 'off', // off | client | server
        mode: 'A',
        quality: 'HQ',
        height: 2160,
        compare: false,
        stats: false
    };

    function loadSettings() {
        try {
            const raw = window.localStorage.getItem(STORAGE_KEY);
            if (raw) {
                return Object.assign({}, defaultSettings, JSON.parse(raw));
            }
        } catch (e) {
            // ignore
        }
        return Object.assign({}, defaultSettings);
    }

    function saveSettings() {
        try {
            window.localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
        } catch (e) {
            // ignore
        }
    }

    const settings = loadSettings();

    // ------------------------------------------------------------------------------------------
    // Server API helpers
    // ------------------------------------------------------------------------------------------

    function apiClient() {
        return window.ApiClient || null;
    }

    function pluginUrl(path, params) {
        const client = apiClient();
        if (client && client.getUrl) {
            return client.getUrl('Anime4K/' + path, params);
        }
        const url = new URL(path, SCRIPT_URL || window.location.href);
        if (params) {
            Object.keys(params).forEach(function (k) {
                url.searchParams.set(k, params[k]);
            });
        }
        return url.toString();
    }

    function apiGet(path, params) {
        const client = apiClient();
        if (!client) {
            return Promise.reject(new Error('ApiClient not available'));
        }
        return client.getJSON(client.getUrl(path, params));
    }

    function apiPost(path, body, params) {
        const client = apiClient();
        if (!client) {
            return Promise.reject(new Error('ApiClient not available'));
        }
        return client.ajax({
            type: 'POST',
            url: client.getUrl(path, params),
            data: body ? JSON.stringify(body) : undefined,
            contentType: 'application/json',
            dataType: 'json'
        });
    }

    let serverOptions = null;

    function refreshServerOptions() {
        return apiGet('Anime4K/Options').then(function (opts) {
            serverOptions = opts;
            if (panel && panel.touched) {
                // never overwrite choices the user is making right now
                return opts;
            }
            if (opts && opts.Device) {
                // the server is the source of truth for server upscaling of this device
                if (opts.Device.ServerUpscaling) {
                    settings.location = 'server';
                    settings.mode = opts.Device.Mode || settings.mode;
                    settings.quality = opts.Device.Quality || settings.quality;
                    settings.height = opts.Device.TargetHeight || settings.height;
                } else if (settings.location === 'server') {
                    settings.location = 'off';
                }
                saveSettings();
            } else if (settings.location === 'server') {
                settings.location = 'off';
                saveSettings();
            }
            return opts;
        }).catch(function (e) {
            warn('Could not load options', e);
            return null;
        });
    }

    const shaderCache = {};

    function fetchShaderBundle(mode, quality) {
        const key = mode + '|' + quality;
        if (!shaderCache[key]) {
            shaderCache[key] = fetch(pluginUrl('Shaders/Bundle', { mode: mode, quality: quality }), { credentials: 'same-origin' })
                .then(function (r) {
                    if (!r.ok) {
                        throw new Error('HTTP ' + r.status);
                    }
                    return r.text();
                })
                .catch(function (e) {
                    delete shaderCache[key];
                    throw e;
                });
        }
        return shaderCache[key];
    }

    // ------------------------------------------------------------------------------------------
    // mpv user shader parser + RPN evaluation
    // ------------------------------------------------------------------------------------------

    function parseUserShader(source) {
        const lines = source.replace(/\r\n?/g, '\n').split('\n');
        const passes = [];
        let cur = null;
        let hasCode = false;
        for (let i = 0; i < lines.length; i++) {
            const line = lines[i];
            const t = line.trim();
            if (t.indexOf('//!') === 0) {
                if (!cur || hasCode) {
                    cur = { desc: '', hooks: [], binds: [], save: null, width: null, height: null, when: null, components: 4, code: [], unsupported: false };
                    passes.push(cur);
                    hasCode = false;
                }
                const m = /^\/\/!(\w+)\s*(.*)$/.exec(t);
                if (!m) {
                    continue;
                }
                const key = m[1].toUpperCase();
                const value = m[2].trim();
                switch (key) {
                    case 'DESC': cur.desc = value; break;
                    case 'HOOK': cur.hooks.push(value); break;
                    case 'BIND': cur.binds.push(value); break;
                    case 'SAVE': cur.save = value; break;
                    case 'WIDTH': cur.width = value; break;
                    case 'HEIGHT': cur.height = value; break;
                    case 'WHEN': cur.when = value; break;
                    case 'COMPONENTS': cur.components = parseInt(value, 10) || 4; break;
                    case 'OFFSET': break;
                    default: cur.unsupported = true; break; // COMPUTE, TEXTURE, PARAM ...
                }
            } else if (cur) {
                cur.code.push(line);
                if (t && t.indexOf('//') !== 0) {
                    hasCode = true;
                }
            }
        }
        return passes.filter(function (p) {
            return p.hooks.length > 0 && !p.unsupported;
        });
    }

    function evalRpn(expr, sizes) {
        if (!expr) {
            return null;
        }
        const stack = [];
        const tokens = expr.split(/\s+/).filter(Boolean);
        for (let i = 0; i < tokens.length; i++) {
            const tok = tokens[i];
            const num = Number(tok);
            if (!isNaN(num)) {
                stack.push(num);
                continue;
            }
            const dim = /^(\w+)\.(w|h|width|height)$/.exec(tok);
            if (dim) {
                const s = sizes[dim[1]];
                if (!s) {
                    throw new Error('Unknown texture in expression: ' + tok);
                }
                stack.push(dim[2][0] === 'w' ? s[0] : s[1]);
                continue;
            }
            if (tok === '!') {
                stack.push(stack.pop() ? 0 : 1);
                continue;
            }
            const b = stack.pop();
            const a = stack.pop();
            switch (tok) {
                case '+': stack.push(a + b); break;
                case '-': stack.push(a - b); break;
                case '*': stack.push(a * b); break;
                case '/': stack.push(a / b); break;
                case '>': stack.push(a > b ? 1 : 0); break;
                case '<': stack.push(a < b ? 1 : 0); break;
                case '=': stack.push(a === b ? 1 : 0); break;
                default: throw new Error('Unknown RPN operator: ' + tok);
            }
        }
        return stack.pop();
    }

    // ------------------------------------------------------------------------------------------
    // WebGL2 renderer
    // ------------------------------------------------------------------------------------------

    const STAGE_ORDER = ['LUMA', 'CHROMA', 'RGB', 'NATIVE', 'MAIN', 'LINEAR', 'SIGMOID', 'PREKERNEL', 'POSTKERNEL', 'SCALED', 'OUTPUT'];

    const VERTEX_SHADER = '#version 300 es\n'
        + 'in vec2 a_pos;\n'
        + 'out vec2 v_pos;\n'
        + 'void main() {\n'
        + '    v_pos = a_pos * 0.5 + 0.5;\n'
        + '    gl_Position = vec4(a_pos, 0.0, 1.0);\n'
        + '}\n';

    const BLIT_SHADER = '#version 300 es\n'
        + 'precision highp float;\n'
        + 'in vec2 v_pos;\n'
        + 'out vec4 out_color;\n'
        + 'uniform sampler2D u_tex;\n'
        + 'uniform sampler2D u_orig;\n'
        + 'uniform float u_split;\n'
        + 'void main() {\n'
        + '    vec2 p = vec2(v_pos.x, 1.0 - v_pos.y);\n'
        + '    vec4 c = (p.x < u_split) ? texture(u_orig, p) : texture(u_tex, p);\n'
        + '    if (u_split > 0.0 && abs(p.x - u_split) < 0.0015) { c = vec4(1.0, 0.8, 0.2, 1.0); }\n'
        + '    out_color = vec4(clamp(c.rgb, 0.0, 1.0), 1.0);\n'
        + '}\n';

    function buildFragmentSource(pass, hooked) {
        const names = {};
        names.HOOKED = true;
        // the hooked texture is also available under the name of its stage (e.g. MAIN)
        pass.hooks.forEach(function (h) {
            names[h] = true;
        });
        pass.binds.forEach(function (b) {
            names[b] = true;
        });
        let header = '#version 300 es\n'
            + 'precision highp float;\n'
            + 'precision highp int;\n'
            + 'precision highp sampler2D;\n'
            + 'in vec2 v_pos;\n'
            + 'out vec4 a4k_out_color;\n'
            + 'uniform vec2 target_size;\n'
            + 'uniform int frame;\n'
            + 'uniform float random;\n';
        Object.keys(names).forEach(function (n) {
            header += 'uniform sampler2D ' + n + '_raw;\n'
                + 'uniform vec2 ' + n + '_size;\n'
                + 'uniform vec2 ' + n + '_pt;\n'
                + '#define ' + n + '_pos v_pos\n'
                + '#define ' + n + '_mul 1.0\n'
                + '#define ' + n + '_off vec2(0.0)\n'
                + '#define ' + n + '_tex(a4k_p) texture(' + n + '_raw, (a4k_p))\n'
                + '#define ' + n + '_texOff(a4k_o) texture(' + n + '_raw, v_pos + ' + n + '_pt * vec2(a4k_o))\n';
        });
        header += '#line 1\n';
        return header + pass.code.join('\n') + '\nvoid main() { a4k_out_color = hook(); }\n';
    }

    function Anime4KRenderer(video, canvas) {
        this.video = video;
        this.canvas = canvas;
        this.passes = [];
        this.frame = 0;
        this.split = 0;
        this.textures = {};
        this.lastError = null;
        this.stats = { fps: 0, frames: 0, lastStatTime: performance.now(), gpuMs: null, executed: 0 };
        const gl = canvas.getContext('webgl2', { alpha: false, antialias: false, depth: false, stencil: false, premultipliedAlpha: false, preserveDrawingBuffer: false, powerPreference: 'high-performance', desynchronized: true });
        if (!gl) {
            throw new Error('WebGL2 wird von diesem Browser/Gerät nicht unterstützt.');
        }
        this.gl = gl;
        this.floatRender = !!(gl.getExtension('EXT_color_buffer_float') || gl.getExtension('EXT_color_buffer_half_float'));
        if (!this.floatRender) {
            throw new Error('Dieses Gerät kann nicht in Float-Texturen rendern (EXT_color_buffer_float fehlt). Bitte Server-Upscaling verwenden.');
        }
        this.timerExt = gl.getExtension('EXT_disjoint_timer_query_webgl2');
        this.pendingQueries = [];

        const buf = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, buf);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
        this.vbo = buf;

        this.inputTex = this.createTexture(gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE, 2, 2);
        this.inputSize = [0, 0];
        this.blit = this.createProgram(BLIT_SHADER);
        this.fbo = gl.createFramebuffer();
    }

    Anime4KRenderer.prototype.createTexture = function (internalFormat, format, type, w, h) {
        const gl = this.gl;
        const tex = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        gl.texImage2D(gl.TEXTURE_2D, 0, internalFormat, w, h, 0, format, type, null);
        return tex;
    };

    Anime4KRenderer.prototype.createProgram = function (fragmentSource) {
        const gl = this.gl;
        function compile(type, src) {
            const sh = gl.createShader(type);
            gl.shaderSource(sh, src);
            gl.compileShader(sh);
            if (!gl.getShaderParameter(sh, gl.COMPILE_STATUS)) {
                const info = gl.getShaderInfoLog(sh);
                gl.deleteShader(sh);
                throw new Error('Shader-Kompilierung fehlgeschlagen: ' + info);
            }
            return sh;
        }
        const vs = compile(gl.VERTEX_SHADER, VERTEX_SHADER);
        const fs = compile(gl.FRAGMENT_SHADER, fragmentSource);
        const prog = gl.createProgram();
        gl.attachShader(prog, vs);
        gl.attachShader(prog, fs);
        gl.bindAttribLocation(prog, 0, 'a_pos');
        gl.linkProgram(prog);
        gl.deleteShader(vs);
        gl.deleteShader(fs);
        if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
            throw new Error('Shader-Linking fehlgeschlagen: ' + gl.getProgramInfoLog(prog));
        }
        const uniforms = {};
        const count = gl.getProgramParameter(prog, gl.ACTIVE_UNIFORMS);
        for (let i = 0; i < count; i++) {
            const info = gl.getActiveUniform(prog, i);
            uniforms[info.name] = gl.getUniformLocation(prog, info.name);
        }
        return { program: prog, uniforms: uniforms };
    };

    Anime4KRenderer.prototype.setShaders = function (source) {
        const gl = this.gl;
        this.disposePasses();
        const parsed = parseUserShader(source);
        const self = this;
        this.passes = parsed.map(function (p) {
            const prog = self.createProgram(buildFragmentSource(p));
            return Object.assign({}, p, { prog: prog, out: null, outSize: [0, 0] });
        });
        gl.useProgram(null);
        log('Shader geladen:', this.passes.length, 'Pässe');
    };

    Anime4KRenderer.prototype.disposePasses = function () {
        const gl = this.gl;
        this.passes.forEach(function (p) {
            gl.deleteProgram(p.prog.program);
            if (p.out) {
                gl.deleteTexture(p.out);
            }
        });
        this.passes = [];
    };

    Anime4KRenderer.prototype.ensurePassTarget = function (pass, w, h) {
        const gl = this.gl;
        if (!pass.out || pass.outSize[0] !== w || pass.outSize[1] !== h) {
            if (pass.out) {
                gl.deleteTexture(pass.out);
            }
            pass.out = this.createTexture(gl.RGBA16F, gl.RGBA, gl.HALF_FLOAT, w, h);
            pass.outSize = [w, h];
        }
        return pass.out;
    };

    Anime4KRenderer.prototype.drawTriangle = function () {
        const gl = this.gl;
        gl.bindBuffer(gl.ARRAY_BUFFER, this.vbo);
        gl.enableVertexAttribArray(0);
        gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
    };

    /**
     * Renders the current video frame. Returns false when nothing could be rendered.
     */
    Anime4KRenderer.prototype.render = function () {
        const gl = this.gl;
        const video = this.video;
        const vw = video.videoWidth;
        const vh = video.videoHeight;
        if (!vw || !vh || video.readyState < 2) {
            return false;
        }

        // canvas size = displayed area in device pixels
        const dpr = window.devicePixelRatio || 1;
        const cw = Math.max(1, Math.round(this.canvas.clientWidth * dpr));
        const ch = Math.max(1, Math.round(this.canvas.clientHeight * dpr));
        if (this.canvas.width !== cw || this.canvas.height !== ch) {
            this.canvas.width = cw;
            this.canvas.height = ch;
        }
        const scale = Math.min(cw / vw, ch / vh);
        const outW = Math.max(1, Math.round(vw * scale));
        const outH = Math.max(1, Math.round(vh * scale));
        const offX = Math.floor((cw - outW) / 2);
        const offY = Math.floor((ch - outH) / 2);

        // upload frame
        gl.bindTexture(gl.TEXTURE_2D, this.inputTex);
        gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
        gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
        if (this.inputSize[0] !== vw || this.inputSize[1] !== vh) {
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE, video);
            this.inputSize = [vw, vh];
        } else {
            gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, gl.RGBA, gl.UNSIGNED_BYTE, video);
        }

        let query = null;
        if (this.timerExt && this.stats.wanted && this.pendingQueries.length < 3) {
            query = gl.createQuery();
            gl.beginQuery(this.timerExt.TIME_ELAPSED_EXT, query);
        }

        const sizes = { OUTPUT: [outW, outH], NATIVE: [vw, vh] };
        const tex = { NATIVE: this.inputTex };
        let current = this.inputTex;
        let currentSize = [vw, vh];
        let executed = 0;

        gl.bindFramebuffer(gl.FRAMEBUFFER, this.fbo);
        for (let s = 0; s < STAGE_ORDER.length; s++) {
            const stage = STAGE_ORDER[s];
            for (let i = 0; i < this.passes.length; i++) {
                const pass = this.passes[i];
                if (pass.hooks.indexOf(stage) === -1) {
                    continue;
                }
                sizes.MAIN = currentSize;
                sizes.HOOKED = currentSize;
                sizes[stage] = currentSize;
                tex.MAIN = current;
                tex.HOOKED = current;
                tex[stage] = current;

                if (pass.when) {
                    let ok;
                    try {
                        ok = evalRpn(pass.when, sizes);
                    } catch (e) {
                        ok = 0;
                    }
                    if (!ok) {
                        continue;
                    }
                }
                const w = Math.max(1, Math.round(pass.width ? evalRpn(pass.width, sizes) : currentSize[0]));
                const h = Math.max(1, Math.round(pass.height ? evalRpn(pass.height, sizes) : currentSize[1]));
                const target = this.ensurePassTarget(pass, w, h);
                gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, target, 0);
                gl.viewport(0, 0, w, h);
                const prog = pass.prog;
                gl.useProgram(prog.program);

                let unit = 0;
                const bindNames = ['HOOKED', stage].concat(pass.binds);
                for (let b = 0; b < bindNames.length; b++) {
                    const name = bindNames[b];
                    const loc = prog.uniforms[name + '_raw'];
                    if (loc === undefined) {
                        continue;
                    }
                    const t = tex[name];
                    const sz = sizes[name];
                    if (!t || !sz) {
                        throw new Error('Textur ' + name + ' nicht verfügbar (' + pass.desc + ')');
                    }
                    gl.activeTexture(gl.TEXTURE0 + unit);
                    gl.bindTexture(gl.TEXTURE_2D, t);
                    gl.uniform1i(loc, unit);
                    if (prog.uniforms[name + '_size']) {
                        gl.uniform2f(prog.uniforms[name + '_size'], sz[0], sz[1]);
                    }
                    if (prog.uniforms[name + '_pt']) {
                        gl.uniform2f(prog.uniforms[name + '_pt'], 1 / sz[0], 1 / sz[1]);
                    }
                    unit++;
                }
                if (prog.uniforms.target_size) {
                    gl.uniform2f(prog.uniforms.target_size, outW, outH);
                }
                if (prog.uniforms.frame) {
                    gl.uniform1i(prog.uniforms.frame, this.frame);
                }
                if (prog.uniforms.random) {
                    gl.uniform1f(prog.uniforms.random, Math.random());
                }
                this.drawTriangle();
                executed++;

                const save = pass.save || stage;
                if (save === stage || save === 'MAIN' || save === 'HOOKED') {
                    current = target;
                    currentSize = [w, h];
                } else {
                    tex[save] = target;
                    sizes[save] = [w, h];
                }
                // unbind to avoid feedback loops in the next pass
                for (let u = 0; u < unit; u++) {
                    gl.activeTexture(gl.TEXTURE0 + u);
                    gl.bindTexture(gl.TEXTURE_2D, null);
                }
            }
        }

        // final blit to the canvas
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        gl.viewport(0, 0, cw, ch);
        gl.clearColor(0, 0, 0, 1);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.viewport(offX, ch - offY - outH, outW, outH);
        gl.useProgram(this.blit.program);
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, current);
        gl.uniform1i(this.blit.uniforms.u_tex, 0);
        gl.activeTexture(gl.TEXTURE1);
        gl.bindTexture(gl.TEXTURE_2D, this.inputTex);
        gl.uniform1i(this.blit.uniforms.u_orig, 1);
        gl.uniform1f(this.blit.uniforms.u_split, this.split);
        this.drawTriangle();

        if (query) {
            gl.endQuery(this.timerExt.TIME_ELAPSED_EXT);
            this.pendingQueries.push(query);
        }
        this.collectQueries();

        this.frame++;
        this.stats.executed = executed;
        this.stats.frames++;
        this.stats.input = vw + '×' + vh;
        this.stats.output = outW + '×' + outH;
        this.stats.result = currentSize[0] + '×' + currentSize[1];
        const now = performance.now();
        if (now - this.stats.lastStatTime >= 1000) {
            this.stats.fps = this.stats.frames * 1000 / (now - this.stats.lastStatTime);
            this.stats.frames = 0;
            this.stats.lastStatTime = now;
        }
        return true;
    };

    Anime4KRenderer.prototype.collectQueries = function () {
        const gl = this.gl;
        if (!this.timerExt) {
            return;
        }
        while (this.pendingQueries.length) {
            const q = this.pendingQueries[0];
            const available = gl.getQueryParameter(q, gl.QUERY_RESULT_AVAILABLE);
            const disjoint = gl.getParameter(this.timerExt.GPU_DISJOINT_EXT);
            if (!available && !disjoint) {
                break;
            }
            if (available && !disjoint) {
                const ns = gl.getQueryParameter(q, gl.QUERY_RESULT);
                this.stats.gpuMs = ns / 1e6;
            }
            gl.deleteQuery(q);
            this.pendingQueries.shift();
        }
    };

    Anime4KRenderer.prototype.dispose = function () {
        const gl = this.gl;
        try {
            this.disposePasses();
            gl.deleteTexture(this.inputTex);
            gl.deleteProgram(this.blit.program);
            gl.deleteFramebuffer(this.fbo);
            gl.deleteBuffer(this.vbo);
            const lose = gl.getExtension('WEBGL_lose_context');
            if (lose) {
                lose.loseContext();
            }
        } catch (e) {
            // ignore
        }
    };

    // ------------------------------------------------------------------------------------------
    // Styles
    // ------------------------------------------------------------------------------------------

    const CSS = ''
        + '.a4k-canvas{position:absolute;left:0;top:0;width:100%;height:100%;z-index:1;pointer-events:none;background:#000;}'
        + '.videoPlayerContainer .libassjs-canvas-parent{z-index:2;}'
        + '.videoPlayerContainer .videoSubtitles{z-index:2;}'
        + '.a4k-cues{position:absolute;left:0;right:0;bottom:8%;z-index:2;text-align:center;pointer-events:none;color:#fff;font-size:170%;'
        + 'text-shadow:0.14em 0.14em 0.14em #000,0 0 0.2em #000;line-height:1.3;white-space:pre-line;}'
        + '.a4k-cues span{display:inline-block;max-width:70%;}'
        + '.a4k-stats{position:absolute;left:1em;top:1em;z-index:3;pointer-events:none;background:rgba(0,0,0,.65);color:#fff;'
        + 'font:12px/1.5 monospace;padding:.5em .8em;border-radius:.4em;white-space:pre;}'
        + '.a4k-toast{position:fixed;left:50%;top:6%;transform:translateX(-50%);z-index:100001;background:rgba(20,20,20,.92);color:#fff;'
        + 'padding:.7em 1.4em;border-radius:2em;font-size:1.1em;pointer-events:none;opacity:0;transition:opacity .25s;box-shadow:0 4px 20px rgba(0,0,0,.5);}'
        + '.a4k-toast.a4k-show{opacity:1;}'
        + '.a4k-panel-backdrop{position:fixed;inset:0;z-index:100000;background:rgba(0,0,0,.35);}'
        + '.a4k-panel{position:fixed;right:2vw;top:50%;transform:translateY(-50%);z-index:100000;width:min(34em,92vw);max-height:90vh;overflow:auto;'
        + 'background:rgba(24,24,28,.97);color:#fff;border-radius:.8em;box-shadow:0 10px 40px rgba(0,0,0,.6);padding:1em 0;font-size:1.05em;}'
        + '.a4k-title{display:flex;align-items:center;gap:.5em;padding:0 1.2em .6em;font-size:1.3em;font-weight:600;}'
        + '.a4k-title small{font-size:.6em;font-weight:400;opacity:.6;margin-left:auto;}'
        + '.a4k-row{display:flex;align-items:center;justify-content:space-between;gap:1em;padding:.75em 1.2em;cursor:pointer;outline:none;border-left:4px solid transparent;}'
        + '.a4k-row:hover{background:rgba(255,255,255,.06);}'
        + '.a4k-row.a4k-focus,.a4k-row:focus{background:rgba(0,164,220,.25);border-left-color:#00a4dc;}'
        + '.a4k-row.a4k-disabled{opacity:.4;}'
        + '.a4k-label{flex:1;min-width:0;}'
        + '.a4k-label div:last-child{font-size:.78em;opacity:.65;margin-top:.15em;}'
        + '.a4k-value{display:flex;align-items:center;gap:.4em;white-space:nowrap;font-weight:600;}'
        + '.a4k-arrow{opacity:.55;font-size:1.2em;padding:0 .2em;user-select:none;}'
        + '.a4k-arrow:hover{opacity:1;}'
        + '.a4k-button{justify-content:center;font-weight:600;margin:.3em 1.2em;border-radius:.4em;background:rgba(255,255,255,.08);border-left:none;}'
        + '.a4k-button.a4k-primary{background:#00a4dc;}'
        + '.a4k-button.a4k-focus,.a4k-button:focus{box-shadow:0 0 0 3px #fff;background:rgba(0,164,220,.55);}'
        + '.a4k-hint{padding:.6em 1.2em 0;font-size:.78em;opacity:.6;line-height:1.5;}'
        + '.a4k-note{margin:.3em 1.2em;padding:.6em .8em;border-radius:.4em;background:rgba(255,190,0,.15);font-size:.85em;line-height:1.4;}'
        + '.a4k-key{display:inline-block;width:.9em;height:.9em;border-radius:50%;vertical-align:-.1em;margin:0 .25em 0 .6em;}'
        + '.btnAnime4K.a4k-active .material-icons{color:#00a4dc;}';

    function injectCss() {
        if (document.getElementById('a4k-style')) {
            return;
        }
        const style = document.createElement('style');
        style.id = 'a4k-style';
        style.textContent = CSS;
        document.head.appendChild(style);
    }

    // ------------------------------------------------------------------------------------------
    // Toast
    // ------------------------------------------------------------------------------------------

    let toastEl = null;
    let toastTimer = null;

    function toast(text, ms) {
        if (!toastEl) {
            toastEl = document.createElement('div');
            toastEl.className = 'a4k-toast';
            document.body.appendChild(toastEl);
        }
        toastEl.textContent = text;
        toastEl.classList.add('a4k-show');
        clearTimeout(toastTimer);
        toastTimer = setTimeout(function () {
            toastEl.classList.remove('a4k-show');
        }, ms || 2200);
    }

    // ------------------------------------------------------------------------------------------
    // Native text track mirror (native ::cue subtitles are drawn inside the <video> element and
    // would be hidden by the canvas, so they are rendered as HTML above it)
    // ------------------------------------------------------------------------------------------

    function CueMirror(video, container) {
        this.video = video;
        this.el = document.createElement('div');
        this.el.className = 'a4k-cues';
        container.appendChild(this.el);
        this.hidden = [];
        this.onChange = this.update.bind(this);
        this.timer = setInterval(this.sync.bind(this), 500);
        this.sync();
    }

    CueMirror.prototype.sync = function () {
        const tracks = this.video.textTracks;
        if (!tracks) {
            return;
        }
        for (let i = 0; i < tracks.length; i++) {
            const t = tracks[i];
            if (t.mode === 'showing' && (t.kind === 'subtitles' || t.kind === 'captions')) {
                t.mode = 'hidden';
                t.addEventListener('cuechange', this.onChange);
                this.hidden.push(t);
            }
        }
        // forget tracks that were disabled by Jellyfin
        this.hidden = this.hidden.filter(function (t) {
            return t.mode === 'hidden';
        });
        this.update();
    };

    CueMirror.prototype.update = function () {
        const parts = [];
        this.hidden.forEach(function (t) {
            const cues = t.activeCues;
            if (!cues) {
                return;
            }
            for (let i = 0; i < cues.length; i++) {
                const cue = cues[i];
                let text = cue.text || '';
                text = text.replace(/<[^>]+>/g, '');
                if (text) {
                    parts.push(text);
                }
            }
        });
        const html = parts.map(function (p) {
            const span = document.createElement('span');
            span.textContent = p;
            return span.outerHTML;
        }).join('<br>');
        if (this.el.innerHTML !== html) {
            this.el.innerHTML = html;
        }
    };

    CueMirror.prototype.dispose = function () {
        clearInterval(this.timer);
        const self = this;
        this.hidden.forEach(function (t) {
            t.removeEventListener('cuechange', self.onChange);
            if (t.mode === 'hidden') {
                t.mode = 'showing';
            }
        });
        this.hidden = [];
        if (this.el.parentNode) {
            this.el.parentNode.removeChild(this.el);
        }
    };

    // ------------------------------------------------------------------------------------------
    // Player controller (one per <video> element)
    // ------------------------------------------------------------------------------------------

    let controller = null;

    function PlayerController(video) {
        this.video = video;
        this.container = video.parentNode;
        this.renderer = null;
        this.canvas = null;
        this.cues = null;
        this.statsEl = null;
        this.running = false;
        this.dirty = true;
        this.loadedKey = null;
        this.loading = null;
        this.onFrameBound = this.onFrame.bind(this);
        this.onTickBound = this.onTick.bind(this);
        this.markDirty = function () {
            this.dirty = true;
        }.bind(this);
        ['seeked', 'loadeddata', 'resize', 'play', 'pause'].forEach(function (ev) {
            video.addEventListener(ev, this.markDirty);
        }, this);
        window.addEventListener('resize', this.markDirty);
        const self = this;
        this.statsTimer = setInterval(function () {
            self.updateStats(true);
        }, 1000);
    }

    PlayerController.prototype.isServerStream = function () {
        return !!(serverOptions && serverOptions.ActiveJob);
    };

    PlayerController.prototype.apply = function () {
        const wantClient = settings.location === 'client';
        if (!wantClient) {
            this.stop();
            this.updateStats(true);
            return Promise.resolve();
        }
        return this.start();
    };

    PlayerController.prototype.start = function () {
        const self = this;
        const key = settings.mode + '|' + settings.quality;
        if (!this.canvas) {
            this.canvas = document.createElement('canvas');
            this.canvas.className = 'a4k-canvas';
            this.canvas.addEventListener('webglcontextlost', function (e) {
                e.preventDefault();
                if (e.target.a4kDisposed) {
                    return; // we released the context ourselves
                }
                warn('WebGL context lost');
                self.stop();
                toast('Anime4K: Grafikkontext verloren – bitte erneut aktivieren', 4000);
            });
            // directly above the video, below subtitles and the OSD
            if (this.video.nextSibling) {
                this.container.insertBefore(this.canvas, this.video.nextSibling);
            } else {
                this.container.appendChild(this.canvas);
            }
        }
        if (!this.renderer) {
            try {
                this.renderer = new Anime4KRenderer(this.video, this.canvas);
            } catch (e) {
                warn(e);
                this.stop();
                toast('Anime4K: ' + e.message, 6000);
                return Promise.resolve();
            }
        }
        if (!this.cues) {
            this.cues = new CueMirror(this.video, this.container);
        }
        this.renderer.split = settings.compare ? 0.5 : 0;

        if (this.loadedKey === key) {
            this.startLoop();
            return Promise.resolve();
        }
        if (this.loading && this.loading.key === key) {
            return this.loading.promise;
        }
        const promise = fetchShaderBundle(settings.mode, settings.quality).then(function (src) {
            // ignore outdated results (the user switched mode/quality while loading)
            if (!self.renderer || settings.location !== 'client' || key !== settings.mode + '|' + settings.quality) {
                return;
            }
            const t0 = performance.now();
            self.renderer.setShaders(src);
            log('Kompiliert in', Math.round(performance.now() - t0), 'ms');
            self.loadedKey = key;
            self.dirty = true;
            self.startLoop();
        }).catch(function (e) {
            warn(e);
            toast('Anime4K konnte nicht gestartet werden: ' + e.message, 6000);
            self.stop();
        }).then(function () {
            if (self.loading && self.loading.key === key) {
                self.loading = null;
            }
        });
        this.loading = { key: key, promise: promise };
        return promise;
    };

    PlayerController.prototype.startLoop = function () {
        if (this.running) {
            return;
        }
        this.running = true;
        this.dirty = true;
        if (this.video.requestVideoFrameCallback) {
            this.vfc = this.video.requestVideoFrameCallback(this.onFrameBound);
        }
        this.raf = requestAnimationFrame(this.onTickBound);
    };

    PlayerController.prototype.onFrame = function () {
        if (!this.running) {
            return;
        }
        this.dirty = true;
        this.vfc = this.video.requestVideoFrameCallback(this.onFrameBound);
    };

    PlayerController.prototype.onTick = function () {
        if (!this.running) {
            return;
        }
        this.raf = requestAnimationFrame(this.onTickBound);
        const video = this.video;
        if (!video.requestVideoFrameCallback && !video.paused && video.currentTime !== this.lastTime) {
            this.dirty = true;
        }
        if (this.canvas && (this.canvas.clientWidth * (window.devicePixelRatio || 1) | 0) !== this.lastCw) {
            this.dirty = true;
        }
        if (!this.dirty || !this.renderer || !this.renderer.passes.length) {
            return;
        }
        this.dirty = false;
        this.lastTime = video.currentTime;
        this.lastCw = (this.canvas.clientWidth * (window.devicePixelRatio || 1)) | 0;
        try {
            this.renderer.stats.wanted = settings.stats;
            this.renderer.render();
        } catch (e) {
            warn(e);
            toast('Anime4K Fehler: ' + e.message, 6000);
            settings.location = 'off';
            saveSettings();
            this.stop();
            refreshButton();
        }
        this.updateStats();
    };

    PlayerController.prototype.updateStats = function (force) {
        if (!settings.stats || !this.container) {
            if (this.statsEl) {
                this.statsEl.remove();
                this.statsEl = null;
            }
            return;
        }
        if (!this.statsEl) {
            this.statsEl = document.createElement('div');
            this.statsEl.className = 'a4k-stats';
            this.container.appendChild(this.statsEl);
        }
        const now = performance.now();
        if (!force && this.lastStatsUpdate && now - this.lastStatsUpdate < 500) {
            return;
        }
        this.lastStatsUpdate = now;
        let text = 'Anime4K ';
        if (settings.location === 'client' && this.renderer) {
            const s = this.renderer.stats;
            const modeName = (MODES.find(function (m) { return m.id === settings.mode; }) || {}).name;
            text += '– Browser (WebGL2)\n'
                + 'Modus:     ' + modeName + ' / ' + settings.quality + '\n'
                + 'Eingang:   ' + (s.input || '-') + '\n'
                + 'Ergebnis:  ' + (s.result || '-') + '\n'
                + 'Anzeige:   ' + (s.output || '-') + '\n'
                + 'Pässe:     ' + s.executed + '/' + this.renderer.passes.length + '\n'
                + 'Bilder/s:  ' + s.fps.toFixed(1) + '\n'
                + 'GPU-Zeit:  ' + (s.gpuMs != null ? s.gpuMs.toFixed(2) + ' ms' : 'n/v');
            const q = this.video.getVideoPlaybackQuality ? this.video.getVideoPlaybackQuality() : null;
            if (q) {
                text += '\nVerworfen: ' + q.droppedVideoFrames + '/' + q.totalVideoFrames;
            }
        } else if (settings.location === 'server') {
            if (!this.lastOptionsRefresh || now - this.lastOptionsRefresh > 10000) {
                this.lastOptionsRefresh = now;
                refreshServerOptions();
            }
            text += '– Server (FFmpeg/libplacebo)\n'
                + 'Stream:    ' + (this.isServerStream() ? 'aktiv' : 'nicht aktiv (Wiedergabe neu starten)') + '\n'
                + 'Video:     ' + this.video.videoWidth + '×' + this.video.videoHeight + '\n'
                + 'Modus:     ' + settings.mode + ' / ' + settings.quality + ' / ' + settings.height + 'p';
        } else {
            text += '– aus';
        }
        this.statsEl.textContent = text;
    };

    PlayerController.prototype.stop = function () {
        this.running = false;
        if (this.canvas) {
            this.canvas.a4kDisposed = true;
        }
        if (this.vfc && this.video.cancelVideoFrameCallback) {
            this.video.cancelVideoFrameCallback(this.vfc);
        }
        cancelAnimationFrame(this.raf);
        if (this.renderer) {
            this.renderer.dispose();
            this.renderer = null;
        }
        this.loadedKey = null;
        if (this.canvas) {
            this.canvas.remove();
            this.canvas = null;
        }
        if (this.cues) {
            this.cues.dispose();
            this.cues = null;
        }
    };

    PlayerController.prototype.dispose = function () {
        clearInterval(this.statsTimer);
        this.stop();
        if (this.statsEl) {
            this.statsEl.remove();
            this.statsEl = null;
        }
        ['seeked', 'loadeddata', 'resize', 'play', 'pause'].forEach(function (ev) {
            this.video.removeEventListener(ev, this.markDirty);
        }, this);
        window.removeEventListener('resize', this.markDirty);
    };

    // ------------------------------------------------------------------------------------------
    // Server side upscaling: settings + restart of the current playback
    // ------------------------------------------------------------------------------------------

    function saveServerDevice(enabled) {
        return apiPost('Anime4K/Devices/Current', {
            ServerUpscaling: enabled,
            Mode: settings.mode,
            Quality: settings.quality,
            TargetHeight: settings.height
        });
    }

    /**
     * Restarts the current playback at the current position so that the server delivers the new stream.
     * Uses the remote control API on our own session, which works with every Jellyfin web client version.
     */
    function restartPlayback() {
        const client = apiClient();
        if (!client) {
            return Promise.reject(new Error('ApiClient fehlt'));
        }
        const deviceId = client.deviceId();
        return client.getJSON(client.getUrl('Sessions', { deviceId: deviceId })).then(function (sessions) {
            const session = (sessions || []).find(function (s) {
                return s.DeviceId === deviceId && s.NowPlayingItem;
            });
            if (!session) {
                throw new Error('Keine laufende Wiedergabe gefunden');
            }
            const state = session.PlayState || {};
            let position = state.PositionTicks || 0;
            if (controller && controller.video && !isNaN(controller.video.currentTime) && controller.video.currentTime > 0 && !position) {
                position = Math.floor(controller.video.currentTime * 10000000);
            }
            const params = {
                playCommand: 'PlayNow',
                itemIds: session.NowPlayingItem.Id,
                startPositionTicks: position
            };
            if (state.MediaSourceId) {
                params.mediaSourceId = state.MediaSourceId;
            }
            if (state.AudioStreamIndex != null) {
                params.audioStreamIndex = state.AudioStreamIndex;
            }
            if (state.SubtitleStreamIndex != null) {
                params.subtitleStreamIndex = state.SubtitleStreamIndex;
            }
            return client.ajax({ type: 'POST', url: client.getUrl('Sessions/' + session.Id + '/Playing', params) });
        });
    }

    // ------------------------------------------------------------------------------------------
    // Settings panel (mouse, touch, keyboard and TV remote)
    // ------------------------------------------------------------------------------------------

    let panel = null;

    function cycle(list, current, dir, key) {
        key = key || 'id';
        let idx = list.findIndex(function (x) {
            return (typeof x === 'object' ? x[key] : x) === current;
        });
        if (idx < 0) {
            idx = 0;
        }
        idx = (idx + dir + list.length) % list.length;
        const v = list[idx];
        return typeof v === 'object' ? v[key] : v;
    }

    function nameOf(list, id) {
        const e = list.find(function (x) {
            return x.id === id;
        });
        return e ? e.name : id;
    }

    function SettingsPanel() {
        this.focusIndex = 0;
        this.pendingServer = null; // snapshot of the server state when the panel opened
        this.onKeyDown = this.onKeyDown.bind(this);
        this.build();
    }

    SettingsPanel.prototype.rows = function () {
        const loc = settings.location;
        const canServer = !!(serverOptions && serverOptions.CanChangeServerUpscaling);
        const serverChanged = this.serverDirty();
        const rows = [
            {
                id: 'location', label: 'Anime4K', desc: loc === 'server'
                    ? 'Der Server rechnet das Bild hoch – ideal für Fernseher'
                    : (loc === 'client' ? 'Die Grafikkarte dieses Geräts rechnet in Echtzeit' : 'Originalbild'),
                value: nameOf(LOCATIONS, loc),
                change: function (dir) {
                    let list = LOCATIONS;
                    if (!canServer) {
                        list = LOCATIONS.filter(function (l) {
                            return l.id !== 'server' || settings.location === 'server';
                        });
                    }
                    settings.location = cycle(list, settings.location, dir);
                }
            },
            {
                id: 'mode', label: 'Modus', desc: (MODES.find(function (m) { return m.id === settings.mode; }) || {}).desc,
                value: nameOf(MODES, settings.mode), disabled: loc === 'off',
                change: function (dir) {
                    settings.mode = cycle(MODES, settings.mode, dir);
                }
            },
            {
                id: 'quality', label: 'Qualität', desc: (QUALITIES.find(function (q) { return q.id === settings.quality; }) || {}).desc,
                value: nameOf(QUALITIES, settings.quality), disabled: loc === 'off',
                change: function (dir) {
                    settings.quality = cycle(QUALITIES, settings.quality, dir);
                }
            }
        ];
        if (loc === 'server') {
            rows.push({
                id: 'height', label: 'Ausgabeauflösung', desc: 'Auflösung, in der der Server das Video an den Fernseher sendet',
                value: settings.height + 'p',
                change: function (dir) {
                    settings.height = cycle(HEIGHTS, settings.height, dir);
                }
            });
        }
        if (loc === 'client') {
            rows.push({
                id: 'compare', label: 'Vergleich', desc: 'Links Original, rechts Anime4K',
                value: settings.compare ? 'An' : 'Aus',
                change: function () {
                    settings.compare = !settings.compare;
                }
            });
        }
        rows.push({
            id: 'stats', label: 'Statistik', desc: 'Auflösung, Bilder/s und GPU-Zeit einblenden',
            value: settings.stats ? 'An' : 'Aus',
            change: function () {
                settings.stats = !settings.stats;
            }
        });
        if (serverChanged) {
            rows.push({
                id: 'apply', button: true, primary: true,
                label: 'Übernehmen & Wiedergabe neu starten'
            });
        }
        rows.push({ id: 'close', button: true, label: 'Schließen' });
        return rows;
    };

    SettingsPanel.prototype.serverState = function () {
        if (settings.location !== 'server') {
            return 'off';
        }
        return [settings.mode, settings.quality, settings.height].join('|');
    };

    SettingsPanel.prototype.serverDirty = function () {
        return this.pendingServer !== null && this.pendingServer !== this.serverState();
    };

    SettingsPanel.prototype.build = function () {
        const self = this;
        this.backdrop = document.createElement('div');
        this.backdrop.className = 'a4k-panel-backdrop';
        this.backdrop.addEventListener('click', function () {
            self.close();
        });
        this.el = document.createElement('div');
        this.el.className = 'a4k-panel';
        this.el.setAttribute('role', 'dialog');
        this.el.setAttribute('aria-label', 'Anime4K Einstellungen');
        document.body.appendChild(this.backdrop);
        document.body.appendChild(this.el);
        window.addEventListener('keydown', this.onKeyDown, true);
        this.pendingServer = this.serverState();
        this.render();
        refreshServerOptions().then(function () {
            if (panel === self) {
                if (!self.touched) {
                    self.pendingServer = self.serverState();
                }
                self.render();
            }
        });
    };

    SettingsPanel.prototype.render = function () {
        const self = this;
        const rows = this.rows();
        this.currentRows = rows;
        if (this.focusIndex >= rows.length) {
            this.focusIndex = rows.length - 1;
        }
        const frag = document.createDocumentFragment();

        const title = document.createElement('div');
        title.className = 'a4k-title';
        title.innerHTML = '<span class="material-icons" aria-hidden="true">auto_awesome</span>Anime4K<small>Upscaling</small>';
        frag.appendChild(title);

        rows.forEach(function (row, index) {
            const el = document.createElement('div');
            el.className = 'a4k-row' + (row.button ? ' a4k-button' : '') + (row.primary ? ' a4k-primary' : '') + (row.disabled ? ' a4k-disabled' : '')
                + (index === self.focusIndex ? ' a4k-focus' : '');
            el.tabIndex = 0;
            el.setAttribute('data-index', index);
            if (row.button) {
                el.textContent = row.label;
                el.setAttribute('role', 'button');
            } else {
                const label = document.createElement('div');
                label.className = 'a4k-label';
                const l1 = document.createElement('div');
                l1.textContent = row.label;
                label.appendChild(l1);
                if (row.desc) {
                    const l2 = document.createElement('div');
                    l2.textContent = row.desc;
                    label.appendChild(l2);
                }
                const value = document.createElement('div');
                value.className = 'a4k-value';
                const left = document.createElement('span');
                left.className = 'a4k-arrow';
                left.textContent = '◀';
                left.addEventListener('click', function (e) {
                    e.stopPropagation();
                    self.focusIndex = index;
                    self.change(-1);
                });
                const val = document.createElement('span');
                val.textContent = row.value;
                const right = document.createElement('span');
                right.className = 'a4k-arrow';
                right.textContent = '▶';
                right.addEventListener('click', function (e) {
                    e.stopPropagation();
                    self.focusIndex = index;
                    self.change(1);
                });
                value.appendChild(left);
                value.appendChild(val);
                value.appendChild(right);
                el.appendChild(label);
                el.appendChild(value);
                el.setAttribute('role', 'button');
                el.setAttribute('aria-label', row.label + ': ' + row.value);
            }
            el.addEventListener('click', function () {
                self.focusIndex = index;
                self.activate();
            });
            frag.appendChild(el);
        });

        if (settings.location === 'server' && serverOptions && !serverOptions.ServerUpscalingEnabled) {
            const note = document.createElement('div');
            note.className = 'a4k-note';
            note.textContent = 'Server-Upscaling ist im Dashboard deaktiviert.';
            frag.appendChild(note);
        } else if (settings.location === 'server' && !this.serverDirty() && serverOptions && !serverOptions.ActiveJob) {
            const note = document.createElement('div');
            note.className = 'a4k-note';
            note.textContent = 'Server-Upscaling ist für dieses Gerät aktiv und wird beim nächsten Start einer Wiedergabe verwendet.';
            frag.appendChild(note);
        }

        const hint = document.createElement('div');
        hint.className = 'a4k-hint';
        hint.innerHTML = '▲▼ Auswahl · ◀▶ Wert ändern · OK bestätigen · Zurück schließt<br>'
            + 'Fernbedienung:<span class="a4k-key" style="background:#e53935"></span>Menü'
            + '<span class="a4k-key" style="background:#43a047"></span>An/Aus'
            + '<span class="a4k-key" style="background:#fdd835"></span>Modus'
            + '<span class="a4k-key" style="background:#1e88e5"></span>Vergleich'
            + '<br>Tastatur: Umschalt+A Menü · Strg+1…6 Modus · Strg+0 Aus';
        frag.appendChild(hint);

        this.el.innerHTML = '';
        this.el.appendChild(frag);
        this.focusRow();
    };

    SettingsPanel.prototype.focusRow = function () {
        const el = this.el.querySelector('[data-index="' + this.focusIndex + '"]');
        if (el) {
            try {
                el.focus({ preventScroll: false });
            } catch (e) {
                el.focus();
            }
            if (el.scrollIntoView) {
                el.scrollIntoView({ block: 'nearest' });
            }
        }
    };

    SettingsPanel.prototype.move = function (dir) {
        const rows = this.currentRows;
        let i = this.focusIndex;
        for (let n = 0; n < rows.length; n++) {
            i = (i + dir + rows.length) % rows.length;
            if (!rows[i].disabled) {
                break;
            }
        }
        this.focusIndex = i;
        this.render();
    };

    SettingsPanel.prototype.change = function (dir) {
        const row = this.currentRows[this.focusIndex];
        if (!row || row.button || row.disabled) {
            return;
        }
        const before = settings.location;
        this.touched = true;
        row.change(dir);
        saveSettings();
        onSettingsChanged(before);
        this.render();
    };

    SettingsPanel.prototype.activate = function () {
        const row = this.currentRows[this.focusIndex];
        if (!row || row.disabled) {
            return;
        }
        if (row.id === 'close') {
            this.close();
            return;
        }
        if (row.id === 'apply') {
            this.applyServer();
            return;
        }
        this.change(1);
    };

    SettingsPanel.prototype.applyServer = function () {
        const self = this;
        const enable = settings.location === 'server';
        saveServerDevice(enable).then(function () {
            self.pendingServer = self.serverState();
            toast(enable ? 'Server-Upscaling aktiviert – Wiedergabe wird neu gestartet …' : 'Server-Upscaling deaktiviert – Wiedergabe wird neu gestartet …', 3000);
            self.close();
            return restartPlayback();
        }).then(function () {
            setTimeout(refreshServerOptions, 4000);
        }).catch(function (e) {
            warn(e);
            toast('Bitte die Wiedergabe manuell neu starten (' + (e && e.message ? e.message : e) + ')', 5000);
        });
    };

    SettingsPanel.prototype.onKeyDown = function (e) {
        const key = e.key;
        const code = e.keyCode;
        let handled = true;
        if (key === 'ArrowUp' || code === 38) {
            this.move(-1);
        } else if (key === 'ArrowDown' || code === 40) {
            this.move(1);
        } else if (key === 'ArrowLeft' || code === 37) {
            this.change(-1);
        } else if (key === 'ArrowRight' || code === 39) {
            this.change(1);
        } else if (key === 'Enter' || key === ' ' || code === 13 || code === 32 || key === 'Select') {
            this.activate();
        } else if (isBackKey(e) || isColorKey(e) === 'red') {
            this.close();
        } else if (key === 'Tab') {
            this.move(e.shiftKey ? -1 : 1);
        } else {
            handled = false;
        }
        if (handled) {
            e.preventDefault();
            e.stopImmediatePropagation();
            e.stopPropagation();
        }
    };

    SettingsPanel.prototype.close = function () {
        window.removeEventListener('keydown', this.onKeyDown, true);
        this.el.remove();
        this.backdrop.remove();
        if (panel === this) {
            panel = null;
        }
        if (this.serverDirty()) {
            toast('Server-Einstellungen wurden nicht übernommen', 3000);
        }
        // give the focus back to the OSD button so the remote keeps working
        const btn = document.querySelector('.btnAnime4K');
        if (btn) {
            try {
                btn.focus();
            } catch (err) {
                // ignore
            }
        }
    };

    function openPanel() {
        if (panel) {
            return;
        }
        injectCss();
        panel = new SettingsPanel();
    }

    function isBackKey(e) {
        return e.key === 'Escape' || e.key === 'GoBack' || e.key === 'BrowserBack' || e.key === 'Back' || e.key === 'Backspace'
            || e.keyCode === 27 || e.keyCode === 8 || e.keyCode === 461 || e.keyCode === 10009;
    }

    function isColorKey(e) {
        const k = e.key;
        switch (e.keyCode) {
            case 403: return 'red';
            case 404: return 'green';
            case 405: return 'yellow';
            case 406: return 'blue';
            default: break;
        }
        if (k === 'ColorF0Red') {
            return 'red';
        }
        if (k === 'ColorF1Green') {
            return 'green';
        }
        if (k === 'ColorF2Yellow') {
            return 'yellow';
        }
        if (k === 'ColorF3Blue') {
            return 'blue';
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------
    // Settings changes
    // ------------------------------------------------------------------------------------------

    function describe() {
        if (settings.location === 'off') {
            return 'Anime4K: aus';
        }
        return 'Anime4K: Modus ' + nameOf(MODES, settings.mode) + ' (' + nameOf(QUALITIES, settings.quality) + ')'
            + (settings.location === 'server' ? ' · Server ' + settings.height + 'p' : '');
    }

    function onSettingsChanged() {
        if (controller) {
            controller.apply();
            if (controller.renderer) {
                controller.renderer.split = settings.compare ? 0.5 : 0;
                controller.dirty = true;
            }
            controller.updateStats(true);
        }
        refreshButton();
    }

    /** Quick actions (shortcuts / colour keys) */
    function quickToggle() {
        if (settings.location === 'server') {
            openPanel();
            return;
        }
        settings.location = settings.location === 'client' ? 'off' : 'client';
        saveSettings();
        onSettingsChanged();
        toast(describe());
    }

    function quickMode(modeId) {
        if (modeId === null) {
            if (settings.location === 'client') {
                settings.location = 'off';
            }
        } else {
            settings.mode = modeId;
            if (settings.location === 'off') {
                settings.location = 'client';
            }
        }
        saveSettings();
        onSettingsChanged();
        if (settings.location === 'server') {
            toast(describe() + ' – im Menü übernehmen', 3000);
            openPanel();
        } else {
            toast(describe());
        }
    }

    function quickCompare() {
        settings.compare = !settings.compare;
        saveSettings();
        onSettingsChanged();
        toast('Vergleich ' + (settings.compare ? 'an' : 'aus'));
    }

    // ------------------------------------------------------------------------------------------
    // OSD button
    // ------------------------------------------------------------------------------------------

    function refreshButton() {
        const btn = document.querySelector('.btnAnime4K');
        if (btn) {
            btn.classList.toggle('a4k-active', settings.location !== 'off');
            btn.title = describe();
        }
    }

    function ensureOsdButton() {
        const osd = document.querySelector('.videoOsdBottom .buttons');
        if (!osd || osd.querySelector('.btnAnime4K')) {
            return;
        }
        let btn;
        try {
            btn = document.createElement('button', { is: 'paper-icon-button-light' });
            btn.setAttribute('is', 'paper-icon-button-light');
        } catch (e) {
            btn = document.createElement('button');
        }
        btn.type = 'button';
        btn.className = 'btnAnime4K autoSize paper-icon-button-light';
        btn.innerHTML = '<span class="xlargePaperIconButton material-icons auto_awesome" aria-hidden="true">auto_awesome</span>';
        btn.setAttribute('aria-label', 'Anime4K');
        btn.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            openPanel();
        });
        const settingsBtn = osd.querySelector('.btnVideoOsdSettings');
        if (settingsBtn) {
            osd.insertBefore(btn, settingsBtn);
        } else {
            osd.appendChild(btn);
        }
        refreshButton();
    }

    // ------------------------------------------------------------------------------------------
    // Global keys (keyboard + TV remote colour buttons) while a video is playing
    // ------------------------------------------------------------------------------------------

    function onGlobalKeyDown(e) {
        if (panel || !controller) {
            return;
        }
        const target = e.target;
        if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)) {
            return;
        }
        const color = isColorKey(e);
        let handled = true;
        if (color === 'red' || (e.shiftKey && !e.ctrlKey && !e.altKey && (e.key === 'A' || e.key === 'a'))) {
            openPanel();
        } else if (color === 'green') {
            quickToggle();
        } else if (color === 'yellow') {
            quickMode(cycle(MODES, settings.mode, 1));
        } else if (color === 'blue') {
            quickCompare();
        } else if (e.ctrlKey && !e.altKey && !e.shiftKey && /^[0-6]$/.test(e.key)) {
            const n = parseInt(e.key, 10);
            quickMode(n === 0 ? null : MODES[n - 1].id);
        } else {
            handled = false;
        }
        if (handled) {
            e.preventDefault();
            e.stopImmediatePropagation();
        }
    }

    // ------------------------------------------------------------------------------------------
    // Detection of the Jellyfin video player
    // ------------------------------------------------------------------------------------------

    function findVideo() {
        const videos = document.querySelectorAll('.videoPlayerContainer video.htmlvideoplayer, .videoPlayerContainer video');
        for (let i = 0; i < videos.length; i++) {
            if (videos[i].isConnected) {
                return videos[i];
            }
        }
        return null;
    }

    let scanScheduled = false;

    function scan() {
        scanScheduled = false;
        const video = findVideo();
        if (controller && controller.video !== video) {
            controller.dispose();
            controller = null;
        }
        if (video && !controller) {
            injectCss();
            controller = new PlayerController(video);
            refreshServerOptions().then(function () {
                if (controller) {
                    controller.apply();
                    refreshButton();
                    if (settings.location !== 'off') {
                        toast(describe() + (settings.location === 'server' && !(serverOptions && serverOptions.ActiveJob) ? ' (wartet auf Server)' : ''));
                    }
                }
            });
        }
        if (video) {
            ensureOsdButton();
        }
    }

    function scheduleScan() {
        if (!scanScheduled) {
            scanScheduled = true;
            setTimeout(scan, 150);
        }
    }

    function init() {
        injectCss();
        const observer = new MutationObserver(scheduleScan);
        observer.observe(document.body, { childList: true, subtree: true });
        window.addEventListener('keydown', onGlobalKeyDown, true);
        scan();
        log('geladen');
    }

    window.__anime4kJellyfin = {
        version: 1,
        settings: settings,
        openPanel: openPanel,
        parseUserShader: parseUserShader,
        evalRpn: evalRpn,
        Renderer: Anime4KRenderer,
        fetchShaderBundle: fetchShaderBundle,
        get controller() {
            return controller;
        }
    };

    if (document.body) {
        init();
    } else {
        document.addEventListener('DOMContentLoaded', init);
    }
}());
