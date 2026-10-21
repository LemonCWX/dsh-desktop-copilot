/**
 * Tool schemas, kept in one file so they can be checked without the Host.
 *
 * The Host validates raw `output.schema` against a strict subset:
 * type / oneOf / properties / required / additionalProperties / items / enum / const,
 * plus annotations. `required` is an array of property names, never a boolean.
 * The same subset is what the model API accepts, so `parameters` must be a real
 * object schema (an empty `{}` is rejected by the provider).
 */

const ACTION_ITEM = {
  type: 'object',
  additionalProperties: false,
  properties: {
    kind: { type: 'string', enum: ['click', 'type', 'key', 'scroll', 'wait', 'open', 'drag'], description: 'What to do. open = new tab with a URL; drag = press at the start point, move, release at the end point.' },
    url: { type: 'string', description: 'Address to open in a new tab (open actions).' },
    id: { type: 'integer', description: 'Element id from the last desktop_capture.' },
    x: { type: 'integer', description: 'Screen x in physical pixels (only when no element fits).' },
    y: { type: 'integer', description: 'Screen y in physical pixels (only when no element fits).' },
    button: { type: 'integer', description: '1 for right click, default left.' },
    count: { type: 'integer', description: '2 for double click.' },
    text: { type: 'string', description: 'Text to type (type actions).' },
    keys: { type: 'string', description: 'Key combo such as ctrl+s or enter (key actions).' },
    delta: { type: 'integer', description: 'Wheel notches; negative scrolls down (scroll actions).' },
    pages: { type: 'number', description: 'Scroll by screenfuls instead of notches (scroll actions); ignores the Windows wheel-lines setting.' },
    toId: { type: 'integer', description: 'Element id to drag onto (drag actions).' },
    toX: { type: 'integer', description: 'End x in physical pixels (drag actions).' },
    toY: { type: 'integer', description: 'End y in physical pixels (drag actions).' },
    ms: { type: 'integer', description: 'Milliseconds to wait (wait actions).' },
  },
}

const IMAGE_REF = {
  type: 'object',
  additionalProperties: true,
  properties: {
    attachmentId: { type: 'string' },
    mediaType: { type: 'string' },
    bytes: { type: 'integer' },
    width: { type: 'integer' },
    height: { type: 'integer' },
    name: { type: 'string' },
  },
}

export const CAPTURE_PARAMETERS = {
  type: 'object',
  additionalProperties: false,
  properties: {
    scope: { type: 'string', enum: ['window', 'full'], description: 'Override the page setting for this capture only: window (front window) or full (whole screen, every window).' },
    image: { type: 'boolean', description: 'Set false to return only the text (element list and OCR) without the screenshot. Cheaper for polling.' },
    region: {
      type: 'object',
      additionalProperties: false,
      description: 'Zoom into one rectangle of the screen (physical pixels). Use it when the full screenshot is too dense to read a small control.',
      properties: {
        x: { type: 'integer' },
        y: { type: 'integer' },
        w: { type: 'integer' },
        h: { type: 'integer' },
      },
    },
  },
  description: 'Optional: scope overrides window/full for this call, region zooms into one rectangle.',
}

export const CAPTURE_OUTPUT = {
  type: 'object',
  additionalProperties: false,
  required: ['summary'],
  properties: {
    summary: { type: 'string', description: 'Window identity plus the element table the model reads.' },
    image: IMAGE_REF,
  },
}

export const STEP_PARAMETERS = {
  type: 'object',
  additionalProperties: false,
  required: ['title', 'actions'],
  properties: {
    title: { type: 'string', description: 'One short line naming this step, shown on the card.' },
    actions: {
      type: 'array',
      description: 'At most 3 actions, executed in order. Prefer element ids.',
      items: ACTION_ITEM,
    },
    mode: { type: 'string', enum: ['manual', 'auto'], description: 'auto skips the card; dangerous actions still stop for confirmation.' },
    reason: { type: 'string', description: 'One sentence of reasoning: why these actions, and what should change on screen. Shown on the card.' },
  },
}

export const STEP_OUTPUT = {
  type: 'object',
  additionalProperties: false,
  required: ['summary', 'choice'],
  properties: {
    summary: { type: 'string' },
    choice: { type: 'string' },
    reason: { type: 'string' },
    results: {
      type: 'array',
      items: {
        type: 'object',
        additionalProperties: true,
        properties: {
          kind: { type: 'string' },
          ok: { type: 'boolean' },
          note: { type: 'string' },
        },
      },
    },
    forcedManual: { type: 'string' },
  },
}

export const CONTROL_PARAMETERS = {
  type: 'object',
  additionalProperties: false,
  required: ['action'],
  properties: {
    action: { type: 'string', enum: ['status', 'stop', 'auto_on', 'auto_off', 'focus', 'enable', 'disable'] },
    pid: { type: 'integer', description: 'Process id to bring to the front (focus only).' },
  },
}

export const CONTROL_OUTPUT = {
  type: 'object',
  additionalProperties: false,
  required: ['summary'],
  properties: { summary: { type: 'string' } },
}
