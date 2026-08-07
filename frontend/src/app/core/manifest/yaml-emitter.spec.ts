import { formatScalarText, isStableScalar, parseScalarText } from './json-value';
import { emitYaml } from './yaml-emitter';

describe('emitYaml', () => {
  it('writes a mapping in block style with two-space indentation', () => {
    expect(emitYaml({ a: 1, b: { c: 'x' } })).toBe('a: 1\nb:\n  c: x\n');
  });

  it('aligns sequence dashes on their key', () => {
    expect(emitYaml({ hosts: ['a', 'b'] })).toBe('hosts:\n- a\n- b\n');
  });

  it('puts the first key of a mapping item on the dash line', () => {
    expect(emitYaml({ items: [{ name: 'a', port: 1 }] })).toBe(
      'items:\n- name: a\n  port: 1\n',
    );
  });

  it('writes empty containers inline rather than as a dangling key', () => {
    expect(emitYaml({ a: {}, b: [] })).toBe('a: {}\nb: []\n');
  });

  it('writes scalars with their JSON type', () => {
    expect(emitYaml({ n: 2.5, b: true, z: null })).toBe('n: 2.5\nb: true\nz: null\n');
  });

  it.each([
    ['12', '"12"'],
    ['true', '"true"'],
    ['null', '"null"'],
    ['', '""'],
    [' padded ', '" padded "'],
    ['a: b', '"a: b"'],
    ['# not a comment', '"# not a comment"'],
    ['- dash', '"- dash"'],
    ['line\nbreak', '"line\\nbreak"'],
  ])('quotes %j so it reads back as a string', (value, expected) => {
    expect(emitYaml({ k: value })).toBe(`k: ${expected}\n`);
  });

  it.each(['ghcr.io/acme/x', 'ghcr.io/acme/x:1.4.0', 'a#b', '2Gi', 'write_pr'])(
    'leaves %s unquoted: none of it would read back as something else',
    (value) => {
      expect(emitYaml({ k: value })).toBe(`k: ${value}\n`);
    },
  );

  it('quotes a key that would not read back as itself', () => {
    expect(emitYaml({ '12': 'x' })).toBe('"12": x\n');
  });

  it('emits nothing for an empty document', () => {
    expect(emitYaml({})).toBe('{}\n');
  });

  it('nests a sequence inside a sequence', () => {
    expect(emitYaml({ m: [['a', 'b']] })).toBe('m:\n- - a\n  - b\n');
  });
});

describe('scalar text rules', () => {
  it.each([
    ['true', true],
    ['false', false],
    ['null', null],
    ['~', null],
    ['12', 12],
    ['-3.5', -3.5],
    ['1e3', 1000],
  ])('reads %s the way YAML would', (text, expected) => {
    expect(parseScalarText(text)).toEqual(expected);
  });

  it.each(['v1.0.0', '2024-01-01', '1_000', ' 12', 'oui', '0x10'])(
    'keeps %s a string',
    (text) => {
      expect(parseScalarText(text)).toBe(text);
    },
  );

  it('formats a scalar back to the text a field would show', () => {
    expect(formatScalarText(200)).toBe('200');
    expect(formatScalarText(true)).toBe('true');
    expect(formatScalarText(null)).toBe('null');
    expect(formatScalarText('x')).toBe('x');
  });

  it.each([200, true, null, 'v1.0.0'])('calls %s a stable scalar', (value) => {
    expect(isStableScalar(value)).toBe(true);
  });

  it.each(['12', 'true', 'null', ''])('calls the string %j unstable', (value) => {
    expect(isStableScalar(value)).toBe(false);
  });
});
