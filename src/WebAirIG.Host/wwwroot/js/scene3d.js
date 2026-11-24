// WebAirIG 3D coverage view (three.js r170, vendored under wwwroot/lib/three).
// Pure view code: everything it draws comes from Scene3DViewModel DTOs (see ViewModels/Scene3DViewModel.cs).
// Coordinates are Godot's, so this matches the IG exactly: world X = East, Y = Up, Z = South;
// entity-local X = right, Y = up, -Z = forward; camera-local -Z = boresight, +Y = image up.
import * as THREE from '../lib/three/three.module.js';
import { OrbitControls } from '../lib/three/addons/controls/OrbitControls.js';
import { GLTFLoader } from '../lib/three/addons/loaders/GLTFLoader.js';
import { OBJLoader } from '../lib/three/addons/loaders/OBJLoader.js';
import { CSS2DRenderer, CSS2DObject } from '../lib/three/addons/renderers/CSS2DRenderer.js';

const DEG = Math.PI / 180;
const OUT_OF_REACH = new THREE.Color('#ef5350');

/** Called from Scene3DView.razor; returns an object whose methods .NET invokes through IJSObjectReference. */
export function create(host, staticScene, options, dotnet) {
    return new Scene3D(host, staticScene, options, dotnet);
}

function basisQuaternion(b, out = new THREE.Quaternion()) {
    const m = new THREE.Matrix4().makeBasis(
        new THREE.Vector3(b[0], b[1], b[2]),
        new THREE.Vector3(b[3], b[4], b[5]),
        new THREE.Vector3(b[6], b[7], b[8]));
    return out.setFromRotationMatrix(m);
}

function disposeTree(obj) {
    obj.traverse(o => {
        // Model clones share geometry with their cached type template; never dispose those.
        if (!o.userData?.shared) o.geometry?.dispose?.();
        const mats = Array.isArray(o.material) ? o.material : o.material ? [o.material] : [];
        // Shared template materials are disposed with their template; only dispose materials we own.
        for (const m of mats) if (m.userData?.owned) m.dispose();
    });
}

function ownedMaterial(m) { m.userData.owned = true; return m; }

/** Simple procedural stand-ins (entity-local axes, nose toward -Z) for types without a browser-loadable model. */
function buildPlaceholder(t, color) {
    const g = new THREE.Group();
    const mat = ownedMaterial(new THREE.MeshStandardMaterial({ color, roughness: 0.55, metalness: 0.15 }));
    const L = Math.max(0.3, t.lengthM), S = Math.max(0.3, t.spanM), H = Math.max(0.1, t.heightM);
    const box = (w, h, d, x = 0, y = 0, z = 0) => {
        const m = new THREE.Mesh(new THREE.BoxGeometry(w, h, d), mat);
        m.position.set(x, y, z);
        g.add(m);
        return m;
    };
    switch (t.placeholderKind) {
        case 'quad': {
            const arm = L * 1.0, th = Math.max(0.03, H * 0.25);
            box(arm, th, th * 1.5).rotation.y = Math.PI / 4;
            box(arm, th, th * 1.5).rotation.y = -Math.PI / 4;
            box(L * 0.3, H * 0.6, L * 0.3);
            const r = L * 0.22;
            for (const [sx, sz] of [[1, 1], [1, -1], [-1, 1], [-1, -1]]) {
                const d = new THREE.Mesh(new THREE.CylinderGeometry(r, r, th * 0.3, 20), mat);
                d.position.set(sx * arm * 0.35, th, sz * arm * 0.35);
                g.add(d);
            }
            break;
        }
        case 'helo': {
            const fh = H * 0.55;
            box(fh * 0.8, fh, L * 0.5, 0, 0, -L * 0.1);
            box(fh * 0.2, fh * 0.25, L * 0.5, 0, fh * 0.2, L * 0.3);
            box(fh * 0.1, H * 0.35, L * 0.08, 0, fh * 0.35, L * 0.52);
            const disc = new THREE.Mesh(new THREE.CylinderGeometry(S / 2, S / 2, 0.05, 32),
                ownedMaterial(new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0.25, depthWrite: false })));
            disc.position.set(0, fh * 0.7, -L * 0.1);
            g.add(disc);
            break;
        }
        case 'missile':
        case 'bomb': {
            // Cylinder body, cruciform tail fins (span S) and, for missiles, smaller mid-body wings.
            const r = (t.placeholderKind === 'bomb' ? 0.065 : 0.03) * L, fin = 0.12 * L, th = 0.02 + r * 0.1;
            const body = new THREE.Mesh(new THREE.CapsuleGeometry(r, Math.max(0.01, L - 2 * r), 4, 16), mat);
            body.rotation.x = Math.PI / 2;
            g.add(body);
            for (const roll of [0, Math.PI / 2]) {
                box(S, th, fin, 0, 0, L * 0.5 - fin * 0.6).rotation.z = roll;
                if (t.placeholderKind === 'missile') box(S * 0.6, th, fin * 0.8, 0, 0, L * 0.05).rotation.z = roll;
            }
            break;
        }
        default: { // airliner / jet / uav
            const thin = t.placeholderKind === 'uav';
            const fw = L * (thin ? 0.07 : 0.09);
            box(fw, fw, L);
            box(S, fw * 0.22, L * (thin ? 0.08 : 0.16), 0, 0, -L * 0.02);
            box(S * 0.36, fw * 0.18, L * 0.08, 0, fw * 0.3, L * 0.44);
            box(fw * 0.16, H * 0.55, L * 0.1, 0, H * 0.3, L * 0.44);
            break;
        }
    }
    return g;
}

class Scene3D {
    constructor(host, st, options, dotnet) {
        this.host = host;
        this.st = st;
        this.dotnet = dotnet;
        this.opts = options;
        this.entities = new Map();   // id -> entity record
        this.templates = new Map();  // typeId -> Promise<Object3D|null>
        this.types = new Map(st.types.map(t => [t.typeId, t]));
        this.camDefs = new Map(st.cameras.map(c => [c.id, c]));
        this.camStates = new Map();
        this.lastFrameTime = performance.now();
        this.disposed = false;
        // Debug handle for the browser console / automated checks (latest view wins).
        window.webairig3d = this;
        this.THREE = THREE;

        this.renderer = new THREE.WebGLRenderer({ antialias: true, logarithmicDepthBuffer: true });
        this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
        this.renderer.domElement.className = 'scene3d-canvas';
        host.appendChild(this.renderer.domElement);

        this.labels = new CSS2DRenderer();
        this.labels.domElement.className = 'scene3d-labels';
        host.appendChild(this.labels.domElement);

        this.compass = document.createElement('div');
        this.compass.className = 'scene3d-compass';
        this.compass.innerHTML = '<div class="needle"><span>N</span></div>';
        host.appendChild(this.compass);

        this.scene = new THREE.Scene();
        this.scene.background = new THREE.Color('#10151b');
        this.camera = new THREE.PerspectiveCamera(45, 1, 0.3, 600000);
        this.controls = new OrbitControls(this.camera, this.renderer.domElement);
        this.controls.enableDamping = true;
        this.controls.dampingFactor = 0.12;
        this.controls.maxPolarAngle = Math.PI * 0.98;

        this.scene.add(new THREE.HemisphereLight(0xdfe8ff, 0x2a3036, 1.6));
        const sun = new THREE.DirectionalLight(0xffffff, 1.6);
        sun.position.set(0.5, 1, 0.5);
        this.scene.add(sun);

        this.buildGround(st.groundSizeM || 240000);

        this.world = new THREE.Group();
        this.scene.add(this.world);
        this.footprints = new THREE.Group();
        this.scene.add(this.footprints);
        this.losLines = new THREE.Group();
        this.scene.add(this.losLines);

        this.selectionMarker = new THREE.Mesh(
            new THREE.SphereGeometry(1, 24, 12),
            new THREE.MeshBasicMaterial({ color: '#ffd54f', wireframe: true, transparent: true, opacity: 0.3 }));
        this.selectionMarker.visible = false;
        this.scene.add(this.selectionMarker);

        this.prevFollow = null;
        this.prevYaw = null;
        this.needsReset = true;

        this.onPointerDown = e => { this.downAt = [e.clientX, e.clientY]; };
        this.onPointerUp = e => this.handleClick(e);
        this.renderer.domElement.addEventListener('pointerdown', this.onPointerDown);
        this.renderer.domElement.addEventListener('pointerup', this.onPointerUp);

        this.resizeObserver = new ResizeObserver(() => this.resize());
        this.resizeObserver.observe(host);
        this.resize();

        this.clock = performance.now();
        const loop = () => {
            if (this.disposed) return;
            this.raf = requestAnimationFrame(loop);
            this.tick();
        };
        this.raf = requestAnimationFrame(loop);
    }

    // ------------------------------------------------------------------ public API (.NET -> JS)

    update(frame) {
        this.lastFrameTime = performance.now();
        const seen = new Set();
        for (const e of frame.entities) {
            seen.add(e.id);
            let rec = this.entities.get(e.id);
            if (!rec || rec.typeId !== e.typeId) {
                if (rec) this.removeEntity(rec);
                rec = this.addEntity(e);
            }
            rec.pos.fromArray(e.position);
            rec.vel.fromArray(e.velocity);
            basisQuaternion(e.basis, rec.targetQuat);
            if (!rec.initialized) { rec.group.quaternion.copy(rec.targetQuat); rec.initialized = true; }
            rec.name = e.name;
        }
        for (const [id, rec] of this.entities) if (!seen.has(id)) this.removeEntity(rec);

        for (const c of frame.cameras) {
            this.camStates.set(c.id, c);
            const rig = this.ownship?.rigs?.get(c.id);
            if (rig) {
                basisQuaternion(c.basis, rig.targetQuat);
                if (!rig.initialized) { rig.group.quaternion.copy(rig.targetQuat); rig.initialized = true; }
                if (rig.fov !== c.fovDeg) { rig.fov = c.fovDeg; this.rebuildFrustum(rig); }
                const on = c.sensorOn;
                rig.frustumLines.material.color.set(c.limitReached ? OUT_OF_REACH : rig.color);
                rig.frustumLines.material.opacity = on ? 0.95 : 0.3;
                rig.frustumFill.material.opacity = on ? 0.10 : 0.03;
            }
        }
        this.rebuildFootprints(frame.cameras);
        this.applySelection();
    }

    setOptions(options) {
        const prev = this.opts;
        this.opts = options;
        if (prev.mode !== options.mode) this.needsReset = true;
        if (prev.symbolScale !== options.symbolScale) for (const rec of this.entities.values()) this.applyScale(rec);
        if (prev.frustumLengthM !== options.frustumLengthM && this.ownship) {
            for (const rig of this.ownship.rigs.values()) { this.rebuildFrustum(rig); this.rebuildFor(rig); }
        }
        for (const rec of this.entities.values()) rec.label.visible = options.showLabels;
        if (this.ownship) for (const rig of this.ownship.rigs.values()) rig.forGroup.visible = options.showFor;
        this.footprints.visible = options.showFootprints;
        this.applySelection();
    }

    resetView() { this.needsReset = true; }

    dispose() {
        this.disposed = true;
        cancelAnimationFrame(this.raf);
        this.resizeObserver.disconnect();
        this.renderer.domElement.removeEventListener('pointerdown', this.onPointerDown);
        this.renderer.domElement.removeEventListener('pointerup', this.onPointerUp);
        this.controls.dispose();
        for (const rec of [...this.entities.values()]) this.removeEntity(rec);
        disposeTree(this.scene);
        this.renderer.dispose();
        this.host.replaceChildren();
    }

    // ------------------------------------------------------------------ scene construction

    buildGround(size) {
        const ground = new THREE.Mesh(new THREE.PlaneGeometry(size, size),
            new THREE.MeshBasicMaterial({ color: '#18212a' }));
        ground.rotation.x = -Math.PI / 2;
        this.scene.add(ground);
        const minor = new THREE.GridHelper(size, Math.round(size / 1000), '#2c3a47', '#22303b');
        minor.material.transparent = true; minor.material.opacity = 0.6;
        minor.position.y = 0.5;
        const major = new THREE.GridHelper(size, Math.round(size / 10000), '#4a6072', '#3b4d5c');
        major.position.y = 1.0;
        this.scene.add(minor, major);
        // North marker at the origin: a short arrow along -Z.
        const arrow = new THREE.ArrowHelper(new THREE.Vector3(0, 0, -1), new THREE.Vector3(0, 2, 0), 3000, 0x90a4ae, 600, 300);
        this.scene.add(arrow);
    }

    typeTemplate(typeId) {
        if (this.templates.has(typeId)) return this.templates.get(typeId);
        const t = this.types.get(typeId);
        let p = Promise.resolve(null);
        if (t?.modelUrl && (t.modelFormat === 'gltf' || t.modelFormat === 'obj')) {
            const url = new URL(t.modelUrl, document.baseURI).href;
            p = (t.modelFormat === 'gltf'
                ? new GLTFLoader().loadAsync(url).then(g => g.scene)
                : new OBJLoader().loadAsync(url).then(obj => {
                    const mat = new THREE.MeshStandardMaterial({ color: '#b0bec5', roughness: 0.6, metalness: 0.2 });
                    obj.traverse(o => { if (o.isMesh) o.material = mat; });
                    return obj;
                }))
                .then(model => {
                    // Same fix-up as the IG's ModelLibrary "ModelFix" node: Godot Euler (YXZ order), uniform scale, FRD offset.
                    const fix = new THREE.Group();
                    const r = t.modelRotationDeg;
                    fix.rotation.set(r[0] * DEG, r[1] * DEG, r[2] * DEG, 'YXZ');
                    fix.scale.setScalar(t.modelScale);
                    fix.position.fromArray(t.modelOffsetLocal);
                    fix.add(model);
                    fix.traverse(o => { o.userData.shared = true; });
                    return fix;
                })
                .catch(err => { console.warn(`WebAirIG 3D: model for type ${typeId} failed, using placeholder`, err); return null; });
        }
        this.templates.set(typeId, p);
        return p;
    }

    addEntity(e) {
        const t = this.types.get(e.typeId) ?? { placeholderKind: 'jet', lengthM: 15, spanM: 12, heightM: 4 };
        const group = new THREE.Group();
        const model = new THREE.Group();
        group.add(model);
        const placeholder = buildPlaceholder(t, e.color);
        model.add(placeholder);

        const labelDiv = document.createElement('div');
        labelDiv.className = 'scene3d-label' + (e.isOwnship ? ' own' : '');
        labelDiv.innerHTML = `<span class="dot" style="background:${e.color}"></span>${e.name}`;
        const label = new CSS2DObject(labelDiv);
        label.center.set(0.5, 1.4);
        label.visible = this.opts.showLabels;
        group.add(label);

        const rec = {
            id: e.id, typeId: e.typeId, type: t, isOwnship: e.isOwnship, color: e.color, name: e.name,
            group, model, label, placeholder,
            pos: new THREE.Vector3().fromArray(e.position), vel: new THREE.Vector3(),
            targetQuat: new THREE.Quaternion(), initialized: false,
        };
        group.position.copy(rec.pos);
        this.world.add(group);
        this.entities.set(e.id, rec);

        this.typeTemplate(e.typeId).then(tpl => {
            if (!tpl || !this.entities.has(e.id) || this.entities.get(e.id) !== rec) return;
            model.remove(placeholder);
            disposeTree(placeholder);
            model.add(tpl.clone(true));
        });

        if (e.isOwnship) {
            rec.rigs = new Map();
            for (const c of this.st.cameras) rec.rigs.set(c.id, this.buildRig(rec, c));
            this.ownship = rec;
            this.needsReset = true;
        }
        this.applyScale(rec);
        return rec;
    }

    removeEntity(rec) {
        this.world.remove(rec.group);
        rec.label.element.remove();
        disposeTree(rec.group);
        this.entities.delete(rec.id);
        if (this.ownship === rec) this.ownship = null;
    }

    applyScale(rec) {
        const s = this.opts.symbolScale;
        rec.model.scale.setScalar(s);
        rec.label.position.set(0, Math.max(rec.type.heightM ?? 4, 2) * s * 0.8, 0);
        if (rec.rigs) for (const rig of rec.rigs.values()) {
            rig.group.position.fromArray(rig.def.mountLocal).multiplyScalar(s);
            rig.forGroup.position.copy(rig.group.position);
            rig.marker.scale.setScalar(s);
        }
    }

    buildRig(own, def) {
        const color = new THREE.Color(def.color);
        const group = new THREE.Group();          // camera frame (gimbal-driven), child of the ownship
        const forGroup = new THREE.Group();       // field of regard (mount-fixed), child of the ownship
        own.group.add(group, forGroup);

        const marker = new THREE.Mesh(new THREE.SphereGeometry(0.7, 16, 8),
            ownedMaterial(new THREE.MeshBasicMaterial({ color })));
        group.add(marker);

        const frustumLines = new THREE.LineSegments(new THREE.BufferGeometry(),
            ownedMaterial(new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.95 })));
        const frustumFill = new THREE.Mesh(new THREE.BufferGeometry(),
            ownedMaterial(new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0.10, side: THREE.DoubleSide, depthWrite: false })));
        group.add(frustumLines, frustumFill);

        const forFill = new THREE.Mesh(new THREE.BufferGeometry(),
            ownedMaterial(new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0.06, side: THREE.DoubleSide, depthWrite: false })));
        const forLines = new THREE.LineSegments(new THREE.BufferGeometry(),
            ownedMaterial(new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.45 })));
        forGroup.add(forFill, forLines);
        forGroup.visible = this.opts.showFor;

        const rig = { def, color, group, forGroup, marker, frustumLines, frustumFill, forFill, forLines,
            fov: 15, targetQuat: new THREE.Quaternion(), initialized: false };
        this.rebuildFrustum(rig);
        this.rebuildFor(rig);
        return rig;
    }

    rebuildFrustum(rig) {
        const L = this.opts.frustumLengthM;
        const tv = Math.tan(rig.fov * DEG / 2), th = tv * (rig.def.aspect || 1);
        const c = [[-th, tv], [th, tv], [th, -tv], [-th, -tv]].map(([x, y]) => [x * L, y * L, -L]);
        const lines = [];
        for (let i = 0; i < 4; i++) {
            lines.push(0, 0, 0, ...c[i]);                 // apex to corner
            lines.push(...c[i], ...c[(i + 1) % 4]);       // far rectangle
        }
        lines.push(0, 0, 0, 0, 0, -L);                    // boresight
        // Small "image up" tick so the frustum's orientation is readable.
        lines.push(0, tv * L, -L, 0, tv * L * 1.25, -L);
        rig.frustumLines.geometry.dispose();
        rig.frustumLines.geometry = new THREE.BufferGeometry().setAttribute('position', new THREE.Float32BufferAttribute(lines, 3));
        const tris = [];
        for (let i = 0; i < 4; i++) tris.push(0, 0, 0, ...c[i], ...c[(i + 1) % 4]);
        tris.push(...c[0], ...c[1], ...c[2], ...c[0], ...c[2], ...c[3]);
        rig.frustumFill.geometry.dispose();
        rig.frustumFill.geometry = new THREE.BufferGeometry().setAttribute('position', new THREE.Float32BufferAttribute(tris, 3));
    }

    rebuildFor(rig) {
        const L = this.opts.frustumLengthM;
        const { forGrid: g, forAzSteps: na, forElSteps: ne } = rig.def;
        const P = (i, j) => { const k = (i * ne + j) * 3; return [g[k] * L, g[k + 1] * L, g[k + 2] * L]; };
        const tris = [], lines = [];
        for (let i = 0; i < na - 1; i++) for (let j = 0; j < ne - 1; j++) {
            const a = P(i, j), b = P(i + 1, j), c = P(i + 1, j + 1), d = P(i, j + 1);
            tris.push(...a, ...b, ...c, ...a, ...c, ...d);
        }
        // Outline, the az = 0 / el = 0 cross, and rays from the mount to the four corners.
        for (let i = 0; i < na - 1; i++) lines.push(...P(i, 0), ...P(i + 1, 0), ...P(i, ne - 1), ...P(i + 1, ne - 1));
        for (let j = 0; j < ne - 1; j++) lines.push(...P(0, j), ...P(0, j + 1), ...P(na - 1, j), ...P(na - 1, j + 1));
        const mi = (na - 1) / 2, mj = (ne - 1) / 2;
        if (Number.isInteger(mi)) for (let j = 0; j < ne - 1; j++) lines.push(...P(mi, j), ...P(mi, j + 1));
        if (Number.isInteger(mj)) for (let i = 0; i < na - 1; i++) lines.push(...P(i, mj), ...P(i + 1, mj));
        for (const [i, j] of [[0, 0], [na - 1, 0], [0, ne - 1], [na - 1, ne - 1]]) lines.push(0, 0, 0, ...P(i, j));
        rig.forFill.geometry.dispose();
        rig.forFill.geometry = new THREE.BufferGeometry().setAttribute('position', new THREE.Float32BufferAttribute(tris, 3));
        rig.forLines.geometry.dispose();
        rig.forLines.geometry = new THREE.BufferGeometry().setAttribute('position', new THREE.Float32BufferAttribute(lines, 3));
    }

    rebuildFootprints(cams) {
        for (const child of [...this.footprints.children]) { this.footprints.remove(child); disposeTree(child); }
        for (const c of cams) {
            const pts = c.footprint;
            if (!pts || pts.length < 9 || !c.sensorOn) continue;
            const def = this.camDefs.get(c.id);
            const color = new THREE.Color(def?.color ?? '#4fc3f7');
            const n = pts.length / 3;
            const ring = [], fan = [];
            for (let i = 0; i < n; i++) ring.push(pts[i * 3], 3, pts[i * 3 + 2]);
            for (let i = 1; i < n - 1; i++) fan.push(ring[0], 3, ring[2], ring[i * 3], 3, ring[i * 3 + 2], ring[(i + 1) * 3], 3, ring[(i + 1) * 3 + 2]);
            const line = new THREE.LineLoop(new THREE.BufferGeometry().setAttribute('position', new THREE.Float32BufferAttribute(ring, 3)),
                ownedMaterial(new THREE.LineBasicMaterial({ color })));
            const fill = new THREE.Mesh(new THREE.BufferGeometry().setAttribute('position', new THREE.Float32BufferAttribute(fan, 3)),
                ownedMaterial(new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0.12, side: THREE.DoubleSide, depthWrite: false })));
            this.footprints.add(line, fill);
        }
    }

    applySelection() {
        for (const rec of this.entities.values())
            rec.label.element.classList.toggle('selected', rec.id === this.opts.selectedEntityId);
    }

    // ------------------------------------------------------------------ per-frame

    tick() {
        const now = performance.now();
        const dt = Math.min(0.1, (now - this.clock) / 1000);
        this.clock = now;
        // Extrapolate from the last 5 Hz snapshot so fast movers (ownship at 140 m/s) move smoothly.
        const since = Math.min(1.0, (now - this.lastFrameTime) / 1000);
        const k = 1 - Math.exp(-dt / 0.12);
        for (const rec of this.entities.values()) {
            rec.group.position.copy(rec.pos).addScaledVector(rec.vel, since);
            rec.group.quaternion.slerp(rec.targetQuat, k);
            if (rec.rigs) for (const rig of rec.rigs.values()) rig.group.quaternion.slerp(rig.targetQuat, k);
        }
        this.updateFollow();
        this.updateLinesOfSight();
        this.updateSelectionMarker();
        this.controls.update();
        this.updateCompass();
        this.renderer.render(this.scene, this.camera);
        this.labels.render(this.scene, this.camera);
    }

    ownYaw() {
        const fwd = new THREE.Vector3(0, 0, -1).applyQuaternion(this.ownship.group.quaternion);
        return Math.atan2(-fwd.x, -fwd.z); // rotation about +Y that maps -Z to the heading
    }

    updateFollow() {
        if (!this.ownship) return;
        const p = this.ownship.group.position;
        if (this.needsReset) {
            this.needsReset = false;
            const tactical = this.opts.mode === 'Tactical';
            // Behind-left-above the ownship in ownship mode; high and behind in tactical mode.
            const offset = tactical ? new THREE.Vector3(-6000, 14000, 18000) : new THREE.Vector3(-75, 45, 105);
            offset.applyAxisAngle(new THREE.Vector3(0, 1, 0), this.ownYaw());
            this.controls.target.copy(p);
            this.camera.position.copy(p).add(offset);
            this.camera.near = tactical ? 5 : 0.3;
            this.camera.updateProjectionMatrix();
            this.prevFollow = p.clone();
            this.prevYaw = this.ownYaw();
            return;
        }
        const delta = p.clone().sub(this.prevFollow);
        this.camera.position.add(delta);
        this.controls.target.add(delta);
        this.prevFollow.copy(p);
        const yaw = this.ownYaw();
        if (this.opts.followHeading && this.prevYaw !== null) {
            let dy = yaw - this.prevYaw;
            if (dy > Math.PI) dy -= 2 * Math.PI; else if (dy < -Math.PI) dy += 2 * Math.PI;
            const off = this.camera.position.clone().sub(this.controls.target).applyAxisAngle(new THREE.Vector3(0, 1, 0), dy);
            this.camera.position.copy(this.controls.target).add(off);
        }
        this.prevYaw = yaw;
    }

    updateLinesOfSight() {
        for (const child of [...this.losLines.children]) { this.losLines.remove(child); disposeTree(child); }
        if (!this.ownship) return;
        for (const [id, c] of this.camStates) {
            if (c.trackEntityId < 0) continue;
            const tgt = this.entities.get(c.trackEntityId);
            const rig = this.ownship.rigs.get(id);
            if (!tgt || !rig) continue;
            const from = rig.group.getWorldPosition(new THREE.Vector3());
            const to = tgt.group.position;
            const geom = new THREE.BufferGeometry().setFromPoints([from, to]);
            const mat = c.limitReached
                ? ownedMaterial(new THREE.LineDashedMaterial({ color: OUT_OF_REACH, dashSize: 40, gapSize: 30 }))
                : ownedMaterial(new THREE.LineBasicMaterial({ color: rig.color }));
            const line = new THREE.Line(geom, mat);
            if (c.limitReached) line.computeLineDistances();
            this.losLines.add(line);
        }
    }

    updateSelectionMarker() {
        const rec = this.entities.get(this.opts.selectedEntityId);
        this.selectionMarker.visible = !!rec;
        if (!rec) return;
        const size = Math.max(rec.type.lengthM ?? 10, rec.type.spanM ?? 10) * this.opts.symbolScale * 0.6;
        this.selectionMarker.position.copy(rec.group.position);
        this.selectionMarker.scale.setScalar(size);
    }

    updateCompass() {
        const dir = this.camera.getWorldDirection(new THREE.Vector3());
        const heading = Math.atan2(dir.x, -dir.z); // 0 = looking north, + = east
        this.compass.firstChild.style.transform = `rotate(${-heading}rad)`;
    }

    resize() {
        const w = Math.max(1, this.host.clientWidth), h = Math.max(1, this.host.clientHeight);
        this.renderer.setSize(w, h, false);
        this.renderer.domElement.style.width = '100%';
        this.renderer.domElement.style.height = '100%';
        this.labels.setSize(w, h);
        this.camera.aspect = w / h;
        this.camera.updateProjectionMatrix();
    }

    handleClick(e) {
        if (!this.downAt || Math.hypot(e.clientX - this.downAt[0], e.clientY - this.downAt[1]) > 4) return;
        const rect = this.renderer.domElement.getBoundingClientRect();
        const x = e.clientX - rect.left, y = e.clientY - rect.top;
        let best = -1, bestD = 28;
        const v = new THREE.Vector3();
        for (const rec of this.entities.values()) {
            v.copy(rec.group.position).project(this.camera);
            if (v.z > 1) continue;
            const sx = (v.x + 1) / 2 * rect.width, sy = (1 - v.y) / 2 * rect.height;
            const d = Math.hypot(sx - x, sy - y);
            if (d < bestD) { bestD = d; best = rec.id; }
        }
        this.dotnet.invokeMethodAsync('OnEntityClicked', best);
    }
}
