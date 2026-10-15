# SPDX-License-Identifier: GPL-3.0-or-later
"""Small OSC 1.0 codec: no external Python packages or background bpy calls."""
import struct


def _string(data, offset):
    end = data.find(b'\0', offset)
    if end < 0:
        raise ValueError('Unterminated OSC string')
    next_offset = (end + 4) & ~3
    if next_offset > len(data):
        raise ValueError('Truncated OSC padding')
    return data[offset:end].decode('utf-8'), next_offset


def decode(data, depth=0):
    if len(data) > 65507 or depth > 8:
        raise ValueError('OSC packet exceeds limits')
    if data.startswith(b'#bundle\0'):
        if len(data) < 16:
            raise ValueError('Truncated OSC bundle')
        offset = 16
        result = []
        while offset < len(data):
            if offset + 4 > len(data):
                raise ValueError('Truncated OSC bundle element')
            size = struct.unpack_from('>i', data, offset)[0]
            offset += 4
            if size <= 0 or offset + size > len(data):
                raise ValueError('Invalid OSC bundle length')
            result.extend(decode(data[offset:offset + size], depth + 1))
            offset += size
        return result
    address, offset = _string(data, 0)
    tags, offset = _string(data, offset)
    if not address.startswith('/') or not tags.startswith(','):
        raise ValueError('Invalid OSC message header')
    args = []
    for tag in tags[1:]:
        if tag in 'ifsd':
            if tag == 's':
                value, offset = _string(data, offset)
            else:
                size = 8 if tag == 'd' else 4
                if offset + size > len(data):
                    raise ValueError('Truncated OSC argument')
                value = struct.unpack_from('>' + tag, data, offset)[0]
                offset += size
            args.append(value)
        elif tag in 'TFN':
            args.append(True if tag == 'T' else False if tag == 'F' else None)
        else:
            raise ValueError('Unsupported OSC type: ' + tag)
    if offset != len(data):
        raise ValueError('Trailing data in OSC message')
    return [(address, args)]


def encode(address, value):
    def padded(text):
        raw = text.encode('utf-8') + b'\0'
        return raw + b'\0' * (-len(raw) % 4)
    if isinstance(value, str):
        tags, payload = ',s', padded(value)
    elif isinstance(value, bool):
        tags, payload = (',T' if value else ',F'), b''
    elif isinstance(value, int):
        tags, payload = ',i', struct.pack('>i', value)
    else:
        tags, payload = ',f', struct.pack('>f', value)
    return padded(address) + padded(tags) + payload
