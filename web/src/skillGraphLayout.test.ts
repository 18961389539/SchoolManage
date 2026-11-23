import { describe, expect, it } from 'vitest'
import type { SkillGraphCategory, SkillGraphSkill } from './analytics'
import {
  GRAPH_CENTER,
  RADAR_CENTER,
  SKILL_MAP_WIDTH,
  buildRadarAxes,
  createRadarGeometry,
  layoutSkillGraph,
  layoutSkillMap,
  shorten
} from './skillGraphLayout'

function skill(id: string, score: number, state: SkillGraphSkill['state'] = 'learning'): SkillGraphSkill {
  return {
    id,
    name: `技能${id}`,
    chapterId: 'c1',
    chapterName: '章节一',
    categoryId: 'cat1',
    categoryName: '专业一',
    score,
    state,
    totalResources: 1,
    learnedCount: 1,
    passedCount: state === 'mastered' ? 1 : 0,
    resources: []
  }
}

function category(id: string, skillsPerChapter: number[]): SkillGraphCategory {
  return {
    id,
    name: `专业${id}`,
    type: 'core',
    score: 80,
    skillCount: skillsPerChapter.reduce((sum, count) => sum + count, 0),
    masteredCount: 0,
    chapters: skillsPerChapter.map((count, chapterIndex) => ({
      id: `${id}-ch${chapterIndex}`,
      name: `章节${chapterIndex + 1}`,
      skills: Array.from({ length: count }, (_, index) => skill(`${id}-${chapterIndex}-${index}`, 60 + index))
    }))
  }
}

const colorOf = (id: string) => `color-${id}`

describe('layoutSkillMap', () => {
  it('lays out categories, chapters and skills in three columns', () => {
    const layout = layoutSkillMap([category('a', [0, 3]), category('b', [1])], colorOf)

    expect(layout.nodes).toHaveLength(2 + 3 + 4)
    expect(layout.links).toHaveLength(3 + 4)
    expect(layout.bands).toHaveLength(2)
    expect(layout.height).toBeGreaterThanOrEqual(400)

    const kinds = layout.nodes.reduce<Record<string, number>>((counts, node) => {
      counts[node.kind] = (counts[node.kind] || 0) + 1
      return counts
    }, {})
    expect(kinds).toEqual({ category: 2, chapter: 3, skill: 4 })

    for (const node of layout.nodes) {
      expect(node.x).toBe({ category: 12, chapter: 238, skill: 476 }[node.kind])
      expect(node.w).toBe({ category: 176, chapter: 188, skill: 306 }[node.kind])
      expect(node.color).toBe(`color-${node.categoryId}`)
    }
    expect(layout.nodes.find(node => node.kind === 'category')?.label).toBe('专业a')
  })

  it('keeps category bands apart and anchors the map width', () => {
    const layout = layoutSkillMap([category('a', [4]), category('b', [2])], colorOf)
    const [first, second] = layout.bands

    expect(first.y).toBe(22)
    expect(second.y).toBeGreaterThanOrEqual(first.y + first.height)
    expect(SKILL_MAP_WIDTH).toBe(800)
  })
})

describe('layoutSkillGraph', () => {
  const categories = [category('a', [2, 1]), category('b', [1])]

  it('spreads categories around the centre when nothing is filtered', () => {
    const { nodes, links } = layoutSkillGraph(categories, '', '', colorOf)

    expect(nodes).toHaveLength(3)
    expect(nodes[0]).toMatchObject({ id: 'graph-root', kind: 'root', ...GRAPH_CENTER, label: '全部课程' })
    expect(nodes[0].subtitle).toBe('')
    expect(links).toHaveLength(2)
    expect(links[0]).toMatchObject({ x1: GRAPH_CENTER.x, y1: GRAPH_CENTER.y })
  })

  it('shows chapters for a category and skills for a chapter', () => {
    const chapterView = layoutSkillGraph(categories, 'a', '', colorOf)
    expect(chapterView.nodes.map(node => node.kind)).toEqual(['root', 'chapter', 'chapter'])
    expect(chapterView.nodes[0].subtitle).toBe('2 章')

    const skillView = layoutSkillGraph(categories, 'a', 'a-ch0', colorOf)
    expect(skillView.nodes.map(node => node.kind)).toEqual(['root', 'skill', 'skill'])
    expect(skillView.nodes[0].subtitle).toBe('2 项技能')
    expect(skillView.nodes[1].color).toBe('#be882f')
  })

  it('shrinks the topic radius when the ring is crowded', () => {
    const crowded = layoutSkillGraph([category('a', [15])], 'a', 'a-ch0', colorOf)
    expect(crowded.nodes[1].r).toBe(28)
    expect(layoutSkillGraph([category('a', [3])], 'a', 'a-ch0', colorOf).nodes[1].r).toBe(40)
  })
})

describe('radar geometry', () => {
  it('averages chapter scores and keeps the strongest eight', () => {
    const axes = buildRadarAxes([
      {
        ...category('a', []),
        chapters: Array.from({ length: 10 }, (_, index) => ({
          id: `ch${index}`,
          name: `章节${index}`,
          skills: [skill(`s${index}-0`, index * 5), skill(`s${index}-1`, index * 5 + 10)]
        }))
      }
    ])

    expect(axes).toHaveLength(8)
    expect(axes[0].score).toBe(50)
    expect(axes[0].label).toBe('章节9')
    expect(axes.every((axis, index) => index === 0 || axis.score <= axes[index - 1].score)).toBe(true)
  })

  it('places the first axis at the top and labels the sides', () => {
    const radar = createRadarGeometry(4)

    expect(radar.angle(0)).toBeCloseTo(-Math.PI / 2)
    expect(radar.point(0, 1).x).toBeCloseTo(RADAR_CENTER.x)
    expect(radar.point(0, 1).y).toBeCloseTo(RADAR_CENTER.y - 188)
    expect(radar.point(1, 1).x).toBeCloseTo(RADAR_CENTER.x + 188)
    expect(radar.polygon(1).split(' ')).toHaveLength(4)
    expect(radar.labelAnchor(0)).toBe('middle')
    expect(radar.labelAnchor(1)).toBe('start')
    expect(radar.labelAnchor(3)).toBe('end')
    expect(radar.labelPoint(0).y).toBeCloseTo(RADAR_CENTER.y - 210)
  })
})

describe('shorten', () => {
  it('adds an ellipsis only when the label is too long', () => {
    expect(shorten('网络基础', 10)).toBe('网络基础')
    expect(shorten('一二三四五六七八九十十一', 10)).toBe('一二三四五六七八九十…')
  })
})
