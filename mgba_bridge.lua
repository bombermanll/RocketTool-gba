-- mGBA TCP memory bridge prototype.
-- Load this script from mGBA's Scripting window, then connect to 127.0.0.1:8765.

local HOST = "127.0.0.1"
local PORT = 8765
local MAX_READ = 4096
local MAX_WRITE = 4096
local MAX_LINE = 8192

local clients = {}
local listener = nil

local PARTY_BASE = 0x02025170
local PARTY_COUNT_OFFSET = -3
local PARTY_MON_SIZE = 100
local PARTY_SLOTS = 6
local EWRAM_START = 0x02000000
local EWRAM_END = 0x02040000
local BATTLE_MON_SIZE = 0x58
local BATTLE_MON_HP_OFFSET = 0x28
local BATTLE_MON_LEVEL_OFFSET = 0x2A
local BATTLE_MON_MAX_HP_OFFSET = 0x2C
local BATTLE_MON_PERSONALITY_OFFSET = 0x48
local ROM_BASE = 0x08000000
local G_MAIN_CALLBACK2 = 0x030032E4
local G_WARP_DESTINATION = 0x020332A0
local LOAD_MAP_FROM_WARP_FUNC = 0x080BA485
local SET_MAIN_CALLBACK2_FUNC = 0x08000541
local CB2_LOAD_MAP_FUNC = 0x080BB901
local TELEPORT_STUB_ADDR = 0x09FFF000
local WALK_THROUGH_WALLS_PATCH_ADDR = 0x080C8A80
local WALK_THROUGH_WALLS_ORIGINAL = { 0xF0, 0xB5, 0x57, 0x46 }
local WALK_THROUGH_WALLS_PATCHED = { 0x00, 0x20, 0x70, 0x47 } -- Thumb: movs r0,#0; bx lr
local SAVE_BLOCK_PTR = 0x0300524C
local REPEL_STEP_OFFSET = 0x402

local cheats = {
    LOCK_HP = false,
    INFINITE_PP = false,
    NO_ENCOUNTER = false,
    WALK_THROUGH_WALLS = false,
    party_base = PARTY_BASE,
    save_base = nil,
}

local cheat_frame = 0
local battle_hp_candidates = {}
local last_battle_hp_scan_frame = -999

local SUBSTRUCTURE_ORDERS = {
    {0, 1, 2, 3}, {0, 1, 3, 2}, {0, 2, 1, 3}, {0, 2, 3, 1},
    {0, 3, 1, 2}, {0, 3, 2, 1}, {1, 0, 2, 3}, {1, 0, 3, 2},
    {1, 2, 0, 3}, {1, 2, 3, 0}, {1, 3, 0, 2}, {1, 3, 2, 0},
    {2, 0, 1, 3}, {2, 0, 3, 1}, {2, 1, 0, 3}, {2, 1, 3, 0},
    {2, 3, 0, 1}, {2, 3, 1, 0}, {3, 0, 1, 2}, {3, 0, 2, 1},
    {3, 1, 0, 2}, {3, 1, 2, 0}, {3, 2, 0, 1}, {3, 2, 1, 0},
}

local function log(msg)
    pcall(function()
        if console then console:log("[mgba-bridge] " .. tostring(msg)) end
    end)
end

local function to_u32(n)
    return n % 0x100000000
end

local function parse_num(s)
    if not s then return nil end
    s = tostring(s):match("^%s*(.-)%s*$")
    if s:match("^0[xX][0-9a-fA-F]+$") then
        return tonumber(s:sub(3), 16)
    end
    if s:match("^[0-9]+$") then
        return tonumber(s, 10)
    end
    return nil
end

local function bytes_to_hex(bytes)
    local out = {}
    for i = 1, #bytes do
        out[i] = string.format("%02X", bytes[i])
    end
    return table.concat(out)
end

local function hex_to_bytes(hex)
    hex = (hex or ""):gsub("%s+", "")
    if #hex % 2 ~= 0 then return nil, "hex length must be even" end
    if not hex:match("^[0-9a-fA-F]*$") then return nil, "hex contains non-hex characters" end
    local bytes = {}
    for i = 1, #hex, 2 do
        bytes[#bytes + 1] = tonumber(hex:sub(i, i + 1), 16)
    end
    return bytes
end

local function ok(text)
    return "OK " .. (text or "") .. "\n"
end

local function err(text)
    return "ERR " .. (text or "unknown error") .. "\n"
end

local function read_range(addr, len)
    local bytes = {}
    for i = 0, len - 1 do
        bytes[#bytes + 1] = emu:read8(addr + i)
    end
    return bytes
end

local function write_range(addr, bytes)
    for i = 1, #bytes do
        emu:write8(addr + i - 1, bytes[i])
    end
end

local function cart0_domain()
    if emu and emu.memory and emu.memory.cart0 then
        return emu.memory.cart0
    end
    return nil
end

local function rom_offset(addr)
    if addr >= ROM_BASE then
        return addr - ROM_BASE
    end
    return addr
end

local function read_rom_range(addr, len)
    local cart0 = cart0_domain()
    if cart0 then
        local offset = rom_offset(addr)
        local bytes = {}
        for i = 0, len - 1 do
            bytes[#bytes + 1] = cart0:read8(offset + i)
        end
        return bytes
    end
    return read_range(addr, len)
end

local function write_rom_range(addr, bytes)
    local cart0 = cart0_domain()
    if cart0 then
        local offset = rom_offset(addr)
        for i = 1, #bytes do
            cart0:write8(offset + i - 1, bytes[i])
        end
        return "cart0"
    end

    -- This usually cannot patch cartridge ROM, but keep it as a compatibility fallback.
    write_range(addr, bytes)
    return "bus"
end

local function bytes_equal(a, b)
    if not a or not b or #a ~= #b then return false end
    for i = 1, #a do
        if a[i] ~= b[i] then return false end
    end
    return true
end

local function walk_through_walls_patch_state()
    if not emu then return "noemu" end
    local current = read_rom_range(WALK_THROUGH_WALLS_PATCH_ADDR, #WALK_THROUGH_WALLS_ORIGINAL)
    if bytes_equal(current, WALK_THROUGH_WALLS_PATCHED) then return "patched" end
    if bytes_equal(current, WALK_THROUGH_WALLS_ORIGINAL) then return "original" end
    return "unknown:" .. bytes_to_hex(current)
end

local function set_walk_through_walls_patch(enabled)
    if not emu then return false, "emu API unavailable" end

    local current = read_rom_range(WALK_THROUGH_WALLS_PATCH_ADDR, #WALK_THROUGH_WALLS_ORIGINAL)
    if enabled then
        if not bytes_equal(current, WALK_THROUGH_WALLS_ORIGINAL) and not bytes_equal(current, WALK_THROUGH_WALLS_PATCHED) then
            return false, "unexpected bytes at collision function: " .. bytes_to_hex(current)
        end
        local method = write_rom_range(WALK_THROUGH_WALLS_PATCH_ADDR, WALK_THROUGH_WALLS_PATCHED)
        local verify = read_rom_range(WALK_THROUGH_WALLS_PATCH_ADDR, #WALK_THROUGH_WALLS_PATCHED)
        if not bytes_equal(verify, WALK_THROUGH_WALLS_PATCHED) then
            return false, "ROM patch verify failed via " .. method .. ": " .. bytes_to_hex(verify)
        end
        return true
    end

    if bytes_equal(current, WALK_THROUGH_WALLS_PATCHED) then
        local method = write_rom_range(WALK_THROUGH_WALLS_PATCH_ADDR, WALK_THROUGH_WALLS_ORIGINAL)
        local verify = read_rom_range(WALK_THROUGH_WALLS_PATCH_ADDR, #WALK_THROUGH_WALLS_ORIGINAL)
        if not bytes_equal(verify, WALK_THROUGH_WALLS_ORIGINAL) then
            return false, "ROM restore verify failed via " .. method .. ": " .. bytes_to_hex(verify)
        end
    end
    return true
end

local function bxor_byte(a, b)
    local out = 0
    local bit = 1
    a = a % 256
    b = b % 256
    while a > 0 or b > 0 do
        local abit = a % 2
        local bbit = b % 2
        if abit ~= bbit then out = out + bit end
        a = math.floor(a / 2)
        b = math.floor(b / 2)
        bit = bit * 2
    end
    return out
end

local function read_u16(addr)
    return emu:read8(addr) + emu:read8(addr + 1) * 0x100
end

local function write_u16(addr, value)
    value = value % 0x10000
    emu:write8(addr, value % 0x100)
    emu:write8(addr + 1, math.floor(value / 0x100) % 0x100)
end

local function write_u32(addr, value)
    value = to_u32(value)
    emu:write8(addr, value % 0x100)
    emu:write8(addr + 1, math.floor(value / 0x100) % 0x100)
    emu:write8(addr + 2, math.floor(value / 0x10000) % 0x100)
    emu:write8(addr + 3, math.floor(value / 0x1000000) % 0x100)
end

local function read_u32(addr)
    return emu:read8(addr) + emu:read8(addr + 1) * 0x100 + emu:read8(addr + 2) * 0x10000 + emu:read8(addr + 3) * 0x1000000
end

local function write_warp_data(addr, map_group, map_num, warp_id, x, y)
    emu:write8(addr, map_group % 0x100)
    emu:write8(addr + 1, map_num % 0x100)
    emu:write8(addr + 2, warp_id % 0x100)
    emu:write8(addr + 3, 0)
    write_u16(addr + 4, x)
    write_u16(addr + 6, y)
end

local function teleport_stub_bytes()
    local bytes = {
        0x10, 0xB5,             -- push {r4, lr}
        0x05, 0x48,             -- ldr r0, [pc, #0x14]
        0xFE, 0x46,             -- mov lr, pc
        0x00, 0x47,             -- bx r0
        0x04, 0x48,             -- ldr r0, [pc, #0x10]
        0x05, 0x49,             -- ldr r1, [pc, #0x14]
        0xFE, 0x46,             -- mov lr, pc
        0x08, 0x47,             -- bx r1
        0x10, 0xBC,             -- pop {r4}
        0x01, 0xBC,             -- pop {r0}
        0x00, 0x47,             -- bx r0
        0x00, 0x00,             -- align literals
    }
    local literals = { LOAD_MAP_FROM_WARP_FUNC, CB2_LOAD_MAP_FUNC, SET_MAIN_CALLBACK2_FUNC }
    for _, value in ipairs(literals) do
        value = to_u32(value)
        bytes[#bytes + 1] = value % 0x100
        bytes[#bytes + 1] = math.floor(value / 0x100) % 0x100
        bytes[#bytes + 1] = math.floor(value / 0x10000) % 0x100
        bytes[#bytes + 1] = math.floor(value / 0x1000000) % 0x100
    end
    return bytes
end

local function request_teleport(map_group, map_num, x, y)
    if not emu then return false, "emu API unavailable" end
    if map_group < 0 or map_group > 255 or map_num < 0 or map_num > 255 then
        return false, "map group/map out of byte range"
    end
    if x < 0 or x > 32767 or y < 0 or y > 32767 then
        return false, "coordinates out of range"
    end

    write_warp_data(G_WARP_DESTINATION, map_group, map_num, 0xFF, x, y)

    local old_callback2 = read_u32(G_MAIN_CALLBACK2)
    if old_callback2 == 0 or old_callback2 == (TELEPORT_STUB_ADDR + 1) then
        return false, string.format("unexpected callback2: 0x%08X", old_callback2)
    end

    local stub = teleport_stub_bytes()
    local method = write_rom_range(TELEPORT_STUB_ADDR, stub)
    local verify = read_rom_range(TELEPORT_STUB_ADDR, #stub)
    if not bytes_equal(verify, stub) then
        return false, "teleport stub write failed via " .. method .. ": " .. bytes_to_hex(verify)
    end

    write_u32(G_MAIN_CALLBACK2, TELEPORT_STUB_ADDR + 1)
    return true, string.format("queued group=%d map=%d x=%d y=%d old_cb=0x%08X load_cb=0x%08X", map_group, map_num, x, y, old_callback2, CB2_LOAD_MAP_FUNC)
end

local function read_u16_from_table(bytes, offset)
    return bytes[offset + 1] + bytes[offset + 2] * 0x100
end

local function write_u16_to_table(bytes, offset, value)
    value = value % 0x10000
    bytes[offset + 1] = value % 0x100
    bytes[offset + 2] = math.floor(value / 0x100) % 0x100
end

local function key_bytes_for_mon(addr)
    local bytes = {}
    for i = 0, 3 do
        bytes[i + 1] = bxor_byte(emu:read8(addr + i), emu:read8(addr + 4 + i))
    end
    return bytes
end

local function decrypt_box_data(addr)
    local key = key_bytes_for_mon(addr)
    local bytes = {}
    for i = 0, 47 do
        bytes[i + 1] = bxor_byte(emu:read8(addr + 0x20 + i), key[(i % 4) + 1])
    end
    return bytes, key
end

local function encrypt_box_data(addr, bytes, key)
    for i = 0, 47 do
        emu:write8(addr + 0x20 + i, bxor_byte(bytes[i + 1], key[(i % 4) + 1]))
    end
end

local function checksum_box_data(bytes)
    local sum = 0
    for i = 0, 46, 2 do
        sum = (sum + read_u16_from_table(bytes, i)) % 0x10000
    end
    return sum
end

local function subblock_offset(addr, substructure)
    local pid = read_u32(addr)
    local order = SUBSTRUCTURE_ORDERS[(pid % 24) + 1]
    for i = 1, 4 do
        if order[i] == substructure then return (i - 1) * 12 end
    end
    return nil
end

local function party_mon_looks_valid(addr)
    local pid = read_u32(addr)
    local otid = read_u32(addr + 4)
    if pid == 0 or otid == 0 then return false end
    local bytes = decrypt_box_data(addr)
    local growth = subblock_offset(addr, 0)
    if not growth then return false end
    local species = read_u16_from_table(bytes, growth)
    return species >= 1 and species <= 2000
end

local function apply_lock_hp(addr)
    if not party_mon_looks_valid(addr) then return end
    local max_hp = read_u16(addr + 0x58)
    if max_hp >= 1 and max_hp <= 999 then
        write_u16(addr + 0x56, max_hp)
    end
end

local function collect_party_pids()
    local pids = {}
    local count = 0
    local base = cheats.party_base or PARTY_BASE
    local party_slots = emu:read8(base + PARTY_COUNT_OFFSET)
    if party_slots < 1 or party_slots > PARTY_SLOTS then
        party_slots = PARTY_SLOTS
    end
    for slot = 0, party_slots - 1 do
        local addr = base + slot * PARTY_MON_SIZE
        if party_mon_looks_valid(addr) then
            local pid = read_u32(addr)
            if pid ~= 0 and not pids[pid] then
                pids[pid] = true
                count = count + 1
            end
        end
    end
    return pids, count
end

local function battle_mon_looks_like_party(addr, party_pids)
    local species = read_u16(addr)
    if species < 1 or species > 2000 then return false end

    local pid = read_u32(addr + BATTLE_MON_PERSONALITY_OFFSET)
    if not party_pids[pid] then return false end

    local hp = read_u16(addr + BATTLE_MON_HP_OFFSET)
    local max_hp = read_u16(addr + BATTLE_MON_MAX_HP_OFFSET)
    if max_hp < 1 or max_hp > 999 or hp > max_hp then return false end

    local level = emu:read8(addr + BATTLE_MON_LEVEL_OFFSET)
    if level < 1 or level > 100 then return false end

    for i = 0, 3 do
        local move = read_u16(addr + 0x0C + i * 2)
        if move > 1000 then return false end
    end

    return true
end

local function scan_battle_hp_candidates(party_pids, start_addr, end_addr)
    local found = {}
    local limit = end_addr - BATTLE_MON_SIZE
    for addr = start_addr, limit, 4 do
        if battle_mon_looks_like_party(addr, party_pids) then
            found[#found + 1] = addr
            if #found >= 12 then break end
        end
    end
    return found
end

local function refresh_battle_hp_candidates(party_pids)
    last_battle_hp_scan_frame = cheat_frame

    -- In Emerald-like ROMs gBattleMons is usually close to, and before, gPlayerParty.
    local party_base = cheats.party_base or PARTY_BASE
    local near_start = math.max(EWRAM_START, party_base - 0x9000)
    local near_end = math.min(EWRAM_END, party_base + 0x2000)
    local found = scan_battle_hp_candidates(party_pids, near_start, near_end)

    -- Fallback for ROM hacks whose battle globals moved further away.
    if #found == 0 and (cheat_frame % 180 == 0) then
        found = scan_battle_hp_candidates(party_pids, EWRAM_START, EWRAM_END)
    end

    battle_hp_candidates = found
end

local function apply_battle_lock_hp(party_pids, party_count)
    if party_count == 0 then return end
    if cheat_frame - last_battle_hp_scan_frame >= 30 then
        refresh_battle_hp_candidates(party_pids)
    end

    local kept = {}
    for i = 1, #battle_hp_candidates do
        local addr = battle_hp_candidates[i]
        if battle_mon_looks_like_party(addr, party_pids) then
            local max_hp = read_u16(addr + BATTLE_MON_MAX_HP_OFFSET)
            write_u16(addr + BATTLE_MON_HP_OFFSET, max_hp)
            kept[#kept + 1] = addr
        end
    end
    battle_hp_candidates = kept
end

local function apply_infinite_pp(addr)
    if not party_mon_looks_valid(addr) then return end
    local bytes, key = decrypt_box_data(addr)
    local attacks = subblock_offset(addr, 1)
    if not attacks then return end
    local changed = false
    for i = 0, 3 do
        local move = read_u16_from_table(bytes, attacks + i * 2)
        if move ~= 0 and bytes[attacks + 8 + i + 1] ~= 99 then
            bytes[attacks + 8 + i + 1] = 99
            changed = true
        end
    end
    if changed then
        write_u16(addr + 0x1C, checksum_box_data(bytes))
        encrypt_box_data(addr, bytes, key)
    end
end

local function current_save_base()
    local base = cheats.save_base or read_u32(SAVE_BLOCK_PTR)
    if base >= 0x02000000 and base < 0x02040000 then return base end
    return nil
end

local function apply_cheats()
    if not emu then return end
    cheat_frame = (cheat_frame + 1) % 1000000

    if cheats.LOCK_HP or cheats.INFINITE_PP then
        local base = cheats.party_base or PARTY_BASE
        for slot = 0, PARTY_SLOTS - 1 do
            local addr = base + slot * PARTY_MON_SIZE
            if cheats.LOCK_HP then apply_lock_hp(addr) end
            if cheats.INFINITE_PP then apply_infinite_pp(addr) end
        end

        if cheats.LOCK_HP then
            local party_pids, party_count = collect_party_pids()
            apply_battle_lock_hp(party_pids, party_count)
        end
    end

    if cheats.NO_ENCOUNTER then
        local base = current_save_base()
        if base then
            emu:write8(base + REPEL_STEP_OFFSET, 250)
        end
    end

    -- WALK_THROUGH_WALLS is a code patch applied when toggled, not a per-frame writer.
end

local function cheat_status()
    return string.format("LOCK_HP=%d INFINITE_PP=%d NO_ENCOUNTER=%d WALK_THROUGH_WALLS=%d WTW_PATCH=%s PARTY_BASE=0x%08X BATTLE_HP=%d SAVE_BASE=%s REPEL_OFFSET=0x%X",
        cheats.LOCK_HP and 1 or 0,
        cheats.INFINITE_PP and 1 or 0,
        cheats.NO_ENCOUNTER and 1 or 0,
        cheats.WALK_THROUGH_WALLS and 1 or 0,
        walk_through_walls_patch_state(),
        cheats.party_base or 0,
        #battle_hp_candidates,
        cheats.save_base and string.format("0x%08X", cheats.save_base) or "auto",
        REPEL_STEP_OFFSET)
end

local function parse_bool(value)
    value = tostring(value or ""):upper()
    return value == "1" or value == "ON" or value == "TRUE" or value == "YES"
end

local function handle_cheat(parts)
    local name = (parts[2] or ""):upper()
    if name == "STATUS" then
        return ok(cheat_status())
    elseif name == "LOCATION" then
        local base = current_save_base()
        if not base then return err("save block not located") end
        local x = read_u16(base)
        local y = read_u16(base + 2)
        local map_group = emu:read8(base + 4)
        local map_num = emu:read8(base + 5)
        return ok(string.format("GROUP=%d MAP=%d X=%d Y=%d SAVE_BASE=0x%08X", map_group, map_num, x, y, base))
    elseif name == "TELEPORT" then
        local map_group = parse_num(parts[3])
        local map_num = parse_num(parts[4])
        local x = parse_num(parts[5])
        local y = parse_num(parts[6])
        if not map_group or not map_num or not x or not y then
            return err("usage: CHEAT TELEPORT <group> <map> <x> <y>")
        end
        local queued, why = request_teleport(map_group, map_num, x, y)
        if not queued then return err("teleport failed: " .. tostring(why)) end
        return ok(tostring(why))
    elseif name == "CLEAR" then
        set_walk_through_walls_patch(false)
        cheats.LOCK_HP = false
        cheats.INFINITE_PP = false
        cheats.NO_ENCOUNTER = false
        cheats.WALK_THROUGH_WALLS = false
        battle_hp_candidates = {}
        return ok(cheat_status())
    elseif name == "PARTY_BASE" then
        local addr = parse_num(parts[3])
        if not addr then return err("usage: CHEAT PARTY_BASE <addr>") end
        cheats.party_base = to_u32(addr)
        return ok(cheat_status())
    elseif name == "SAVE_BASE" then
        local addr = parse_num(parts[3])
        if not addr then return err("usage: CHEAT SAVE_BASE <addr>") end
        cheats.save_base = to_u32(addr)
        return ok(cheat_status())
    elseif name == "REPEL_OFFSET" then
        local offset = parse_num(parts[3])
        if not offset then return err("usage: CHEAT REPEL_OFFSET <offset>") end
        REPEL_STEP_OFFSET = offset
        return ok(cheat_status())
    elseif cheats[name] ~= nil then
        local enabled = parse_bool(parts[3])
        if name == "LOCK_HP" then
            battle_hp_candidates = {}
            last_battle_hp_scan_frame = -999
        elseif name == "WALK_THROUGH_WALLS" then
            local patched, why = set_walk_through_walls_patch(enabled)
            if not patched then return err("walk-through-walls patch failed: " .. tostring(why)) end
        end
        cheats[name] = enabled
        log("cheat " .. name .. "=" .. tostring(cheats[name]))
        return ok(cheat_status())
    end
    return err("usage: CHEAT STATUS|LOCATION|TELEPORT|CLEAR|PARTY_BASE|SAVE_BASE|REPEL_OFFSET|LOCK_HP|INFINITE_PP|NO_ENCOUNTER|WALK_THROUGH_WALLS")
end

local function handle_command(line)
    line = (line or ""):gsub("\r$", "")
    local parts = {}
    for token in line:gmatch("%S+") do
        parts[#parts + 1] = token
    end

    local cmd = (parts[1] or ""):upper()
    if cmd == "" then
        return nil
    elseif cmd == "PING" then
        return ok("PONG")
    elseif cmd == "HELP" then
        return ok("PING INFO GAMECODE READ8 READ16 READ32 READ WRITERANGE WRITE8 WRITE16 WRITE32 CHEAT")
    elseif cmd == "INFO" then
        return ok("mgba-bridge proto=1 max_read=" .. MAX_READ .. " max_write=" .. MAX_WRITE)
    elseif cmd == "GAMECODE" then
        local bytes = read_range(0x080000AC, 4)
        return ok(bytes_to_hex(bytes))
    elseif cmd == "READ" then
        local addr = parse_num(parts[2])
        local len = parse_num(parts[3])
        if not addr or not len then return err("usage: READ <addr> <len>") end
        if len < 0 or len > MAX_READ then return err("length out of range") end
        return ok(bytes_to_hex(read_range(to_u32(addr), len)))
    elseif cmd == "READ8" or cmd == "READ16" or cmd == "READ32" then
        local addr = parse_num(parts[2])
        if not addr then return err("usage: " .. cmd .. " <addr>") end
        local value
        if cmd == "READ8" then value = emu:read8(to_u32(addr))
        elseif cmd == "READ16" then value = emu:read16(to_u32(addr))
        else value = emu:read32(to_u32(addr)) end
        return ok(string.format("0x%X", value))
    elseif cmd == "WRITERANGE" then
        local addr = parse_num(parts[2])
        local bytes, why = hex_to_bytes(parts[3])
        if not addr or not bytes then return err("usage: WRITERANGE <addr> <hex>; " .. (why or "bad hex")) end
        if #bytes > MAX_WRITE then return err("write length out of range") end
        write_range(to_u32(addr), bytes)
        return ok("WROTE " .. #bytes)
    elseif cmd == "WRITE8" or cmd == "WRITE16" or cmd == "WRITE32" then
        local addr = parse_num(parts[2])
        local value = parse_num(parts[3])
        if not addr or not value then return err("usage: " .. cmd .. " <addr> <value>") end
        addr = to_u32(addr)
        if cmd == "WRITE8" then emu:write8(addr, value % 0x100)
        elseif cmd == "WRITE16" then emu:write16(addr, value % 0x10000)
        else emu:write32(addr, to_u32(value)) end
        return ok("WROTE")
    elseif cmd == "CHEAT" then
        return handle_cheat(parts)
    else
        return err("unknown command: " .. cmd)
    end
end

local function send(client, text)
    if not text or #text == 0 then return true end
    if not client or not client.socket then return false, "missing client socket" end
    local success, sent, why = pcall(function()
        return client.socket:send(text)
    end)
    if not success then return false, tostring(why) end
    if sent == false then return false, tostring(why or "send failed") end
    return true
end

local function close_client(index, why)
    local client = clients[index]
    if client then
        pcall(function() client.socket:close() end)
        table.remove(clients, index)
        log("client disconnected" .. (why and (": " .. why) or ""))
    end
end

local function socket_hasdata(sock)
    local success, result = pcall(function()
        return sock and sock:hasdata()
    end)
    if not success then return false, tostring(result) end
    return result and true or false, nil
end

local function accept_clients()
    while listener do
        local has_data, has_error = socket_hasdata(listener)
        if has_error then
            log("listener error: " .. has_error)
            break
        end
        if not has_data then break end

        local accepted, sock = pcall(function()
            return listener:accept()
        end)
        if not accepted then
            log("accept failed: " .. tostring(sock))
            break
        end
        if not sock then break end

        local client = { socket = sock, buffer = "" }
        clients[#clients + 1] = client
        local sent, why = send(client, "OK mgba-bridge ready\n")
        if sent then
            log("client connected")
        else
            close_client(#clients, "welcome send failed: " .. tostring(why))
        end
    end
end

local function process_clients()
    for i = #clients, 1, -1 do
        local client = clients[i]
        while client do
            local has_data, has_error = socket_hasdata(client.socket)
            if has_error then
                close_client(i, "socket error: " .. has_error)
                client = nil
                break
            end
            if not has_data then break end

            local received, chunk = pcall(function()
                return client.socket:receive(1024)
            end)
            if not received then
                close_client(i, "receive error: " .. tostring(chunk))
                client = nil
                break
            end
            if not chunk or #chunk == 0 then
                close_client(i, "closed")
                client = nil
                break
            end
            client.buffer = client.buffer .. chunk
            if #client.buffer > MAX_LINE then
                send(client, err("line too long"))
                close_client(i, "line too long")
                client = nil
                break
            end
            while client do
                local nl = client.buffer:find("\n", 1, true)
                if not nl then break end
                local line = client.buffer:sub(1, nl - 1)
                client.buffer = client.buffer:sub(nl + 1)
                local success, response = pcall(handle_command, line)
                if not success then
                    response = err(response)
                end
                local sent, why = send(client, response)
                if not sent then
                    close_client(i, "send error: " .. tostring(why))
                    client = nil
                    break
                end
            end
        end
    end
end

local frame_errors = {}

local function safe_frame_call(name, fn)
    local success, why = pcall(fn)
    if success then
        frame_errors[name] = nil
        return
    end

    local text = tostring(why)
    if frame_errors[name] ~= text then
        frame_errors[name] = text
        log(name .. " error: " .. text)
    end
end

local function start()
    if not socket then
        error("mGBA socket API is not available. Please load this script in desktop mGBA with scripting socket support enabled.")
    end
    if not callbacks then
        error("mGBA callbacks API is not available. Please load this script from mGBA's Scripting window.")
    end
    listener = socket.bind(HOST, PORT)
    if not listener then
        error("failed to bind " .. HOST .. ":" .. PORT)
    end
    listener:listen(4)
    callbacks:add("frame", function()
        safe_frame_call("cheat", apply_cheats)
        safe_frame_call("accept", accept_clients)
        safe_frame_call("process", process_clients)
    end)
    log("listening on " .. HOST .. ":" .. PORT)
end

start()
