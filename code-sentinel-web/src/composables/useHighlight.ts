/**
 * 语法高亮：按版本与文件侧懒加载 Shiki 高亮器，把文件全文分词并按行号取用。
 * 使用 JavaScript 正则引擎避免引入 WASM，并只注册一个小型语言集合。
 * token 颜色不落内联样式，而是把 grammar scope 映射为语义 class。
 */
import { createHighlighter, type Highlighter } from 'shiki';
import { createJavaScriptRegexEngine } from 'shiki/engine/javascript';

import { getBlob } from '../api/diff';
import type { DiffSide } from '../api/types';

/**
 * token 语义类别，对应 DiffLine 中的固定 class。
 */
export type TokenClass =
  | 'comment'
  | 'string'
  | 'keyword'
  | 'number'
  | 'function'
  | 'type'
  | 'variable'
  | 'tag'
  | 'attribute'
  | 'property'
  | 'operator'
  | 'punctuation'
  | 'default';

/**
 * 单个高亮 token。
 */
export interface HighlightToken {
  /** token 文本 */
  content: string;
  /** 语义类别，用于渲染为固定 class */
  className: TokenClass;
}

/**
 * 单侧高亮结果：行号到 token 列表的映射；null 表示回退纯文本。
 */
export type LineTokens = Map<number, HighlightToken[]>;

/** 超过该字节数的文件不做语法高亮，直接回退纯文本 */
const MAX_HIGHLIGHT_BYTES = 200 * 1024;

/** 需要预加载的语言集合，按文件扩展名取用 */
const LANGS = [
  'ts',
  'js',
  'tsx',
  'jsx',
  'json',
  'java',
  'csharp',
  'xml',
  'html',
  'css',
  'markdown',
  'sql',
  'bash',
  'yaml',
  'python',
  'go',
  'rust',
] as const;

/** 支持高亮的语言 id 联合类型 */
type HighlightLang = (typeof LANGS)[number];

/** 模块级高亮器，首次使用时懒加载 */
let highlighterPromise: Promise<Highlighter> | null = null;

/** Shiki 分词 token 的结构子集，避免直接依赖内部类型 */
interface RawToken {
  /** token 文本 */
  content: string;
  /** 分词解释，含命中的 grammar scope */
  explanation?: { scopes: { scopeName: string }[] }[];
}

/**
 * 懒加载唯一的 Shiki 高亮器，只注册小型语言集合与 github-light 主题。
 * @returns 高亮器 Promise
 */
function getHighlighter(): Promise<Highlighter> {
  if (highlighterPromise === null) {
    highlighterPromise = createHighlighter({
      themes: ['github-light'],
      langs: [...LANGS],
      engine: createJavaScriptRegexEngine(),
    });
  }

  return highlighterPromise;
}

/**
 * 把 grammar scope 列表映射为语义 class；未命中时返回 default。
 * @param scopes scope 名列表
 * @returns 语义类别
 */
export function scopeToClass(scopes: string[]): TokenClass {
  const joined = scopes.join(' ');

  if (joined.includes('comment')) {
    return 'comment';
  }

  if (joined.includes('string') || joined.includes('quoted')) {
    return 'string';
  }

  if (
    joined.includes('entity.name.function')
    || joined.includes('support.function')
    || joined.includes('function-call')
  ) {
    return 'function';
  }

  if (
    joined.includes('entity.name.type')
    || joined.includes('entity.name.class')
    || joined.includes('storage.type')
    || joined.includes('support.class')
  ) {
    return 'type';
  }

  if (joined.includes('constant.numeric')) {
    return 'number';
  }

  if (joined.includes('keyword.operator')) {
    return 'operator';
  }

  if (joined.includes('keyword')) {
    return 'keyword';
  }

  if (joined.includes('entity.name.tag')) {
    return 'tag';
  }

  if (joined.includes('attribute-name')) {
    return 'attribute';
  }

  if (joined.includes('variable.other.property') || joined.includes('property-name')) {
    return 'property';
  }

  if (joined.includes('variable')) {
    return 'variable';
  }

  if (joined.includes('operator')) {
    return 'operator';
  }

  if (joined.includes('punctuation')) {
    return 'punctuation';
  }

  return 'default';
}

/**
 * 按文件扩展名解析 Shiki 语言 id，未知时返回 null。
 * @param path 文件路径
 * @returns 语言 id 或 null
 */
function languageOf(path: string): HighlightLang | null {
  const lower = path.toLowerCase();
  const dot = lower.lastIndexOf('.');
  const ext = dot >= 0 ? lower.slice(dot + 1) : '';

  switch (ext) {
    case 'ts':
    case 'mts':
    case 'cts':
      return 'ts';
    case 'tsx':
      return 'tsx';
    case 'js':
    case 'mjs':
    case 'cjs':
      return 'js';
    case 'jsx':
      return 'jsx';
    case 'json':
      return 'json';
    case 'java':
      return 'java';
    case 'cs':
      return 'csharp';
    case 'xml':
    case 'xsd':
    case 'svg':
      return 'xml';
    case 'html':
    case 'htm':
      return 'html';
    case 'css':
    case 'scss':
    case 'less':
      return 'css';
    case 'md':
      return 'markdown';
    case 'sql':
      return 'sql';
    case 'sh':
      return 'bash';
    case 'yaml':
    case 'yml':
      return 'yaml';
    case 'py':
      return 'python';
    case 'go':
      return 'go';
    case 'rs':
      return 'rust';
    default:
      return null;
  }
}

/**
 * 计算 UTF-8 字节数。
 * @param value 文本
 * @returns 字节数
 */
function byteLength(value: string): number {
  return new TextEncoder().encode(value).length;
}

/**
 * 提取 token 命中的全部 grammar scope 名。
 * @param token 分词 token
 * @returns scope 名列表
 */
function tokenScopes(token: RawToken): string[] {
  return (token.explanation ?? []).flatMap((item) => item.scopes.map((scope) => scope.scopeName));
}

/**
 * 把 Shiki 的二维 token 结果按行号映射，并转换为语义 class。
 * @param tokens Shiki 返回的逐行 token
 * @returns 行号到 token 列表的映射
 */
function toLineTokens(tokens: RawToken[][]): LineTokens {
  const result: LineTokens = new Map();
  tokens.forEach((lineTokens, index) => {
    if (lineTokens.length === 0) {
      return;
    }

    result.set(
      index + 1,
      lineTokens.map((token) => ({
        content: token.content,
        className: scopeToClass(tokenScopes(token)),
      })),
    );
  });

  return result;
}

/**
 * 语法高亮组合式函数。
 * @returns 高亮工具
 */
export function useHighlight(): {
  /** 拉取并高亮某侧全文，按（版本, 侧, 路径）缓存；失败或不可用时回退 null */
  highlightSide: (
    reviewId: string,
    revisionId: string,
    path: string,
    side: DiffSide,
  ) => Promise<LineTokens | null>;
} {
  /** token 缓存，键为 版本|侧|路径 */
  const cache = new Map<string, LineTokens | null>();

  /**
   * 拉取并高亮指定侧全文；二进制、超限、空内容、未知语言、超过 200 KB 或分词异常时静默回退 null。
   * @param reviewId 评审单主键
   * @param revisionId 历史版本主键，空串表示当前版本
   * @param path 文件路径
   * @param side OLD 或 NEW
   * @returns 行号到 token 列表的映射，回退时返回 null
   */
  async function highlightSide(
    reviewId: string,
    revisionId: string,
    path: string,
    side: DiffSide,
  ): Promise<LineTokens | null> {
    const key = `${revisionId}|${side}|${path}`;
    const cached = cache.get(key);
    if (cached !== undefined) {
      return cached;
    }

    const result = await tokenize(reviewId, revisionId, path, side);
    cache.set(key, result);
    return result;
  }

  /**
   * 执行单侧分词，任何一步不满足条件都回退 null。
   * @param reviewId 评审单主键
   * @param revisionId 历史版本主键，空串表示当前版本
   * @param path 文件路径
   * @param side OLD 或 NEW
   * @returns 行号到 token 列表的映射或 null
   */
  async function tokenize(
    reviewId: string,
    revisionId: string,
    path: string,
    side: DiffSide,
  ): Promise<LineTokens | null> {
    const lang = languageOf(path);
    if (lang === null) {
      return null;
    }

    try {
      const blob = await getBlob(reviewId, path, side, revisionId || undefined);
      if (blob.isBinary || blob.isTooLarge || blob.content.length === 0) {
        return null;
      }

      if (byteLength(blob.content) > MAX_HIGHLIGHT_BYTES) {
        return null;
      }

      const highlighter = await getHighlighter();
      const { tokens } = highlighter.codeToTokens(blob.content, {
        lang,
        theme: 'github-light',
        includeExplanation: true,
      });
      return toLineTokens(tokens);
    } catch {
      // 分词失败静默降级为纯文本，不向上抛出
      return null;
    }
  }

  return { highlightSide };
}
