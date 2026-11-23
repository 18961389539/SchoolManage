import type { SkillGraphCategory } from './analytics'

export function shorten(value: string, max: number): string {
  return value.length > max ? `${value.slice(0, max)}…` : value
}

export interface MapNode {
  id: string
  kind: 'category' | 'chapter' | 'skill'
  label: string
  x: number
  y: number
  w: number
  h: number
  color: string
  categoryId: string
  chapterId?: string
  skill?: SkillGraphCategory['chapters'][number]['skills'][number]
  score?: number
  skillCount?: number
  masteredCount?: number
}

export interface MapLink {
  id: string
  d: string
  color: string
}

export interface MapBand {
  id: string
  y: number
  height: number
  color: string
}

export interface MapLayout {
  nodes: MapNode[]
  links: MapLink[]
  bands: MapBand[]
  height: number
}

export const SKILL_MAP_WIDTH = 800

/** Three-column map: categories, their chapters, and each chapter's skills. */
export function layoutSkillMap(categories: SkillGraphCategory[], colorOf: (id: string) => string): MapLayout {
  const nodes: MapNode[] = []
  const links: MapLink[] = []
  const bands: MapBand[] = []
  let cursor = 22
  categories.forEach(category => {
    const color = colorOf(category.id)
    const start = cursor
    let chapterCursor = start + 14
    const placements = category.chapters.map(chapter => {
      const height = Math.max(52, chapter.skills.length * 42)
      const placement = { chapter, top: chapterCursor, height, center: chapterCursor + height / 2 }
      chapterCursor += height + 12
      return placement
    })
    const bandHeight = Math.max(88, chapterCursor - start)
    const categoryCenter = start + bandHeight / 2
    nodes.push({
      id: `category-${category.id}`,
      kind: 'category',
      label: category.name,
      x: 12,
      y: categoryCenter - 27,
      w: 176,
      h: 54,
      color,
      categoryId: category.id,
      score: category.score,
      skillCount: category.skillCount,
      masteredCount: category.masteredCount
    })
    placements.forEach(placement => {
      const chapter = placement.chapter
      nodes.push({
        id: `chapter-${chapter.id}`,
        kind: 'chapter',
        label: chapter.name,
        x: 238,
        y: placement.center - 21,
        w: 188,
        h: 42,
        color,
        categoryId: category.id,
        chapterId: chapter.id,
        skillCount: chapter.skills.length
      })
      links.push({
        id: `link-c-${chapter.id}`,
        d: `M188 ${categoryCenter} C210 ${categoryCenter}, 216 ${placement.center}, 238 ${placement.center}`,
        color
      })
      chapter.skills.forEach((skill, index) => {
        const center = placement.top + 21 + index * 42
        nodes.push({
          id: `skill-${skill.id}`,
          kind: 'skill',
          label: skill.name,
          x: 476,
          y: center - 19,
          w: 306,
          h: 38,
          color,
          categoryId: category.id,
          chapterId: chapter.id,
          skill,
          score: skill.score
        })
        links.push({
          id: `link-s-${skill.id}`,
          d: `M426 ${placement.center} C450 ${placement.center}, 454 ${center}, 476 ${center}`,
          color
        })
      })
    })
    bands.push({ id: category.id, y: start, height: bandHeight, color })
    cursor = start + bandHeight + 20
  })
  return { nodes, links, bands, height: Math.max(400, cursor) }
}

export interface GraphNode {
  id: string
  kind: 'root' | 'category' | 'chapter' | 'skill'
  label: string
  subtitle: string
  x: number
  y: number
  r: number
  color: string
  categoryId: string
  chapterId?: string
  skillId?: string
}

export interface GraphLink {
  id: string
  x1: number
  y1: number
  x2: number
  y2: number
  color: string
}

export const GRAPH_CENTER = { x: 450, y: 288 }

/** Radial graph: a centre node with the current filter's categories, chapters or skills around it. */
export function layoutSkillGraph(
  categories: SkillGraphCategory[],
  categoryFilter: string,
  chapterId: string,
  colorOf: (id: string) => string
): { nodes: GraphNode[]; links: GraphLink[] } {
  const category = categories.find(item => item.id === categoryFilter)
  const chapter = category?.chapters.find(item => item.id === chapterId)
  const center: GraphNode = {
    id: 'graph-root',
    kind: 'root',
    label: shorten(chapter?.name || category?.name || '全部课程', 10),
    subtitle: chapter ? `${chapter.skills.length} 项技能` : category ? `${category.chapters.length} 章` : '',
    ...GRAPH_CENTER,
    r: 56,
    color: category ? colorOf(category.id) : '#327fb2',
    categoryId: category?.id || ''
  }
  const topics =
    chapter && category
      ? chapter.skills.map(skill => ({
          id: skill.id,
          kind: 'skill' as const,
          label: skill.name,
          subtitle: `${skill.score} 分`,
          color: skill.state === 'mastered' ? '#2f9d7a' : skill.state === 'learning' ? '#be882f' : '#8296a8',
          categoryId: category.id,
          skillId: skill.id
        }))
      : category
        ? category.chapters.map(item => ({
            id: item.id,
            kind: 'chapter' as const,
            label: item.name,
            subtitle: `${item.skills.length} 项技能`,
            color: colorOf(category.id),
            categoryId: category.id,
            chapterId: item.id
          }))
        : categories.map(item => ({
            id: item.id,
            kind: 'category' as const,
            label: item.name,
            subtitle: `${item.score} 分`,
            color: colorOf(item.id),
            categoryId: item.id
          }))
  const nodes = topics.map((item, index): GraphNode => {
    const angle = -Math.PI / 2 + (index * Math.PI * 2) / topics.length
    return {
      ...item,
      x: GRAPH_CENTER.x + Math.cos(angle) * 335,
      y: GRAPH_CENTER.y + Math.sin(angle) * 216,
      r: topics.length > 14 ? 28 : 40
    }
  })
  const links: GraphLink[] = nodes.map(node => ({
    id: node.id,
    x1: center.x,
    y1: center.y,
    x2: node.x,
    y2: node.y,
    color: node.color
  }))
  return { nodes: [center, ...nodes], links }
}

export interface RadarAxis {
  key: string
  label: string
  caption: string
  score: number
}

/** The strongest eight chapters, which is what the radar has room to label. */
export function buildRadarAxes(categories: SkillGraphCategory[]): RadarAxis[] {
  const rows = categories.flatMap(category =>
    category.chapters.map(chapter => ({
      key: `${category.id}-${chapter.id}`,
      label: chapter.name,
      caption: category.name,
      score: chapter.skills.length
        ? Math.round(chapter.skills.reduce((sum, skill) => sum + skill.score, 0) / chapter.skills.length)
        : 0
    }))
  )
  return rows.sort((a, b) => b.score - a.score).slice(0, 8)
}

export const RADAR_CENTER = { x: 280, y: 280 }
export const RADAR_RADIUS = 188

export interface RadarGeometry {
  angle(index: number): number
  point(index: number, ratio: number): { x: number; y: number }
  polygon(ratio: number): string
  labelAnchor(index: number): 'start' | 'end' | 'middle'
  labelPoint(index: number): { x: number; y: number }
}

export function createRadarGeometry(axisCount: number): RadarGeometry {
  const angle = (index: number) => -Math.PI / 2 + (index * Math.PI * 2) / axisCount
  const point = (index: number, ratio: number) => ({
    x: RADAR_CENTER.x + Math.cos(angle(index)) * RADAR_RADIUS * ratio,
    y: RADAR_CENTER.y + Math.sin(angle(index)) * RADAR_RADIUS * ratio
  })
  return {
    angle,
    point,
    polygon: ratio =>
      Array.from({ length: axisCount }, (_, index) => {
        const { x, y } = point(index, ratio)
        return `${x.toFixed(1)},${y.toFixed(1)}`
      }).join(' '),
    labelAnchor: index => {
      const x = Math.cos(angle(index))
      if (x > 0.3) return 'start'
      if (x < -0.3) return 'end'
      return 'middle'
    },
    labelPoint: index => ({
      x: RADAR_CENTER.x + Math.cos(angle(index)) * (RADAR_RADIUS + 22),
      y: RADAR_CENTER.y + Math.sin(angle(index)) * (RADAR_RADIUS + 22)
    })
  }
}
