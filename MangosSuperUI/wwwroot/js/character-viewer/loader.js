// Character Viewer — GLB loader.
//
// Loads a character GLB produced by SkinnedGlbWriter and indexes its parts:
//
//   bones        — { 0: Object3D, 1: Object3D, ... }   keyed by M2 bone index
//   attachments  — { 1: Object3D, 2: Object3D, ... }   keyed by attachment semantic ID
//   geosets      — { 1300: SkinnedMesh, 1301: ..., ... } keyed by M2 geoset ID
//
// Naming contract enforced by SkinnedGlbWriter:
//   bone        node.name === "Bone_<m2-index>"
//   attachment  node.name === "Attachment_<semantic-id>"
//   geoset      mesh.name === "Geoset_<id>_c<category>_v<variant>_s<submeshIndex>"
//                            (e.g. "Geoset_1300_c13_v0_s4")
//
// glTF `extras` would be the obvious place for geoset metadata, but
// SharpGLTF.Toolkit 1.0.6 doesn't expose a portable way to set them. So the
// metadata is encoded in the mesh name; this loader parses it and writes the
// fields back to SkinnedMesh.userData so Session C reads userData.geosetId
// exactly as if extras had been used. The contract from Session C's
// perspective is unchanged.

import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { applyBlendSuffix } from './blend-suffix.js';

const _loader = new GLTFLoader();

/**
 * Load a character GLB and index its parts.
 * @param {string} url  Web URL of the .glb (e.g. "/character_models/HumanMale.glb")
 * @returns {Promise<{
 *   gltf: object,
 *   root: THREE.Object3D,
 *   bones: Object<number, THREE.Object3D>,
 *   boneList: THREE.Object3D[],
 *   attachments: Object<number, THREE.Object3D>,
 *   attachmentList: THREE.Object3D[],
 *   geosets: Object<number, THREE.SkinnedMesh>,
 *   geosetList: THREE.SkinnedMesh[],
 *   animations: THREE.AnimationClip[]
 * }>}
 */
export function loadCharacterGlb(url) {
    return new Promise((resolve, reject) => {
        _loader.load(
            url,
            (gltf) => {
                try {
                    // Reconfigure any materials with _blendN suffix (Session M
                    // phase 2.5). Character bodies are typically all opaque so
                    // this is usually a no-op, but it costs nothing and keeps
                    // the loader honest with the writer's contract.
                    applyBlendSuffix(gltf.scene);

                    resolve(indexCharacter(gltf));
                } catch (err) {
                    reject(err);
                }
            },
            undefined,
            (err) => reject(err)
        );
    });
}

/**
 * Walk the loaded scene graph and bucket nodes by role.
 * Returns an object that's stable across this session — no mutation post-resolve.
 */
function indexCharacter(gltf) {
    const root = gltf.scene;

    const bones = {};
    const boneList = [];
    const attachments = {};
    const attachmentList = [];
    const geosets = {};
    const geosetList = [];

    root.traverse((n) => {
        // Bones — both glTF skin joints (n.isBone === true) and our named bone
        // NodeBuilders should be matched. We use the name prefix as the key
        // source because SharpGLTF's joint nodes don't always set isBone reliably
        // in older Toolkit versions, but the name is always preserved.
        if (n.name && n.name.startsWith('Bone_')) {
            const idx = parseInt(n.name.substring(5), 10);
            if (!Number.isNaN(idx)) {
                bones[idx] = n;
                boneList.push(n);
            }
        }

        // Attachments — Attachment_<id>
        if (n.name && n.name.startsWith('Attachment_')) {
            const id = parseInt(n.name.substring(11), 10);
            if (!Number.isNaN(id)) {
                attachments[id] = n;
                attachmentList.push(n);
            }
        }

        // Geosets — SkinnedMesh objects whose name encodes the geoset metadata.
        // Parse the suffix off the mesh name and write the fields to userData so
        // Session C reads `m.userData.geosetId` (the same contract as if extras
        // had worked).
        if (n.isSkinnedMesh && n.name) {
            const meta = parseGeosetName(n.name);
            if (meta) {
                n.userData.geosetId = meta.geosetId;
                n.userData.geosetCategory = meta.geosetCategory;
                n.userData.geosetVariant = meta.geosetVariant;
                n.userData.submeshIndex = meta.submeshIndex;

                // First-wins on duplicates — matches "default geoset rendered,
                // variants hidden" semantics that Session C will lean on. We
                // still push every mesh into geosetList so debug code sees them all.
                if (!(meta.geosetId in geosets)) geosets[meta.geosetId] = n;
                geosetList.push(n);
            }
        }
    });

    // Sanity: warn (don't throw) if we got zero of any expected bucket. Helps
    // debugging when the server hasn't redeployed the new naming yet.
    if (boneList.length === 0) {
        console.warn('[character-viewer] no Bone_* nodes found in GLB — is this a skinned character model?');
    }
    if (geosetList.length === 0) {
        console.warn('[character-viewer] no Geoset_* meshes parsed — server may not have the geoset-naming patch yet');
    }

    // Animations (Session O) — GLTFLoader populates gltf.animations from the
    // glTF's `animations` array as THREE.AnimationClip[]. Surface it on the
    // character object so callers don't have to reach through `.gltf`.
    // Empty array if the server didn't bake any (older v2 character GLBs).
    const animations = gltf.animations || [];
    if (animations.length === 0) {
        console.warn('[character-viewer] no animations in GLB — character will be bind-pose only ' +
            '(server SkinnedGlbVersion may need a bump)');
    } else {
        console.log('[character-viewer] animations:',
            animations.map(c => `${c.name} (${c.duration.toFixed(2)}s)`).join(', '));
    }

    return {
        gltf,
        root,
        bones,
        boneList,
        attachments,
        attachmentList,
        geosets,
        geosetList,
        animations,
    };
}

// Geoset_<id>_c<cat>_v<var>_s<sub> → { geosetId, geosetCategory, geosetVariant, submeshIndex }
// Returns null on any mesh name that doesn't match (lights, helper meshes, etc.).
//
// The category/variant numbers are encoded in the name purely for inspection
// convenience — they're derivable from geosetId (cat = id/100, var = id%100).
// We still use the encoded values rather than recomputing, so if the server
// ever changes the convention (e.g. four-digit geoset IDs in a hypothetical
// expansion patch) the parser stays consistent with whatever it was told.
const _GEOSET_NAME_RE = /^Geoset_(\d+)_c(\d+)_v(\d+)_s(\d+)$/;

function parseGeosetName(name) {
    const m = _GEOSET_NAME_RE.exec(name);
    if (!m) return null;
    return {
        geosetId: parseInt(m[1], 10),
        geosetCategory: parseInt(m[2], 10),
        geosetVariant: parseInt(m[3], 10),
        submeshIndex: parseInt(m[4], 10),
    };
}