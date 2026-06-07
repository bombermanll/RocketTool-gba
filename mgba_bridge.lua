-- mGBA TCP memory bridge prototype.
-- Load this script from mGBA's Scripting window, then connect to 127.0.0.1:8765.

local HOST = "127.0.0.1"
local PORT = 8765
local MAX_READ = 4096
local MAX_WRITE = 4096
local MAX_LINE = 8192

local clients = {}
local listener = nil

local function log(msg)
    console:log("[mgba-bridge] " .. tostring(msg))
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
        return ok("PING INFO GAMECODE READ8 READ16 READ32 READ WRITERANGE WRITE8 WRITE16 WRITE32")
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
    else
        return err("unknown command: " .. cmd)
    end
end

local function send(client, text)
    if text and #text > 0 then
        client.socket:send(text)
    end
end

local function close_client(index, why)
    local client = clients[index]
    if client then
        pcall(function() client.socket:close() end)
        table.remove(clients, index)
        log("client disconnected" .. (why and (": " .. why) or ""))
    end
end

local function accept_clients()
    while listener and listener:hasdata() do
        local sock = listener:accept()
        if not sock then break end
        clients[#clients + 1] = { socket = sock, buffer = "" }
        sock:send("OK mgba-bridge ready\n")
        log("client connected")
    end
end

local function process_clients()
    for i = #clients, 1, -1 do
        local client = clients[i]
        while client.socket:hasdata() do
            local chunk = client.socket:receive(1024)
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
                send(client, response)
            end
        end
    end
end

local function start()
    if not socket or not emu then
        error("This script must run inside mGBA with socket and emu APIs available")
    end
    listener = socket.bind(HOST, PORT)
    if not listener then
        error("failed to bind " .. HOST .. ":" .. PORT)
    end
    listener:listen(4)
    callbacks:add("frame", function()
        accept_clients()
        process_clients()
    end)
    log("listening on " .. HOST .. ":" .. PORT)
end

start()
