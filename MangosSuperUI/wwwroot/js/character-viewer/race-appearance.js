// Original ChrRaces flags are read from mounted MPQs; armor names never determine this policy.
const RACE_IDS = Object.freeze({ Human: 1, Orc: 2, Dwarf: 3, NightElf: 4, Scourge: 5,
    Undead: 5, Tauren: 6, Gnome: 7, Troll: 8, Goblin: 9, BloodElf: 10, Draenei: 11 });
let policyRequest;

export function appearanceForModelUrl(url, races) {
    const match = String(url).match(/\/character_models\/([^/.]+)(?:\.v\d+)?\.glb(?:[?#].*)?$/i);
    if (!match) return null;
    const raceName = match[1].replace(/(?:Male|Female)$/i, '');
    const id = Object.entries(RACE_IDS).find(([name]) => name.toLowerCase() === raceName.toLowerCase())?.[1];
    return races.find(row => row.id === id) ?? null;
}

export async function loadRaceAppearance(url) {
    // Non-character GLBs have no wearer policy. Normal character URLs must resolve a DBC row.
    if (!String(url).includes('/character_models/')) return null;
    policyRequest ??= fetch('/CharacterAppearance/Races').then(async response => {
        if (!response.ok) throw new Error(`Race appearance policy: HTTP ${response.status}`);
        return response.json();
    }).catch(error => { policyRequest = null; throw error; });
    const policy = await policyRequest;
    const row = appearanceForModelUrl(url, policy.races);
    if (!row) throw new Error('Character race is absent from mounted ChrRaces.dbc.');
    return { ...row, dbcSha256: policy.sha256 };
}
