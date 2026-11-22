<script setup lang="ts">
import type { HomeLeaderboardRow } from '../analytics'
import TableEmpty from './TableEmpty.vue'

defineProps<{ rows: HomeLeaderboardRow[]; teacher?: boolean }>()
</script>

<template>
  <section class="surface home-leaderboard" aria-labelledby="home-leaderboard-title">
    <header class="card-heading">
      <div>
        <h2 id="home-leaderboard-title">{{ teacher ? '学生学习排行' : '班级学习榜' }}</h2>
        <p>按已学习资源数和达标资源数排序</p>
      </div>
    </header>
    <ol v-if="rows.length" class="leaderboard" role="list">
      <li v-for="(row, index) in rows" :key="row.userId" class="leaderboard-row" :class="{ 'is-self': row.isMe }">
        <span
          class="leaderboard-rank"
          :class="{ 'is-first': index === 0, 'is-second': index === 1, 'is-third': index === 2 }"
          >{{ index + 1 }}</span
        >
        <div class="leaderboard-person">
          <strong>{{ row.name }} <span v-if="row.isMe" class="leaderboard-self">我</span></strong>
          <small>{{
            teacher ? `${row.className || '未分班'} · ${row.number}` : `${row.number} · 活跃 ${row.activeDays} 天`
          }}</small>
        </div>
        <div class="leaderboard-value">
          <strong>{{ row.learned }}</strong><span>已学习</span>
        </div>
        <div class="leaderboard-value">
          <strong>{{ row.passRate }}%</strong><span>达标率</span>
        </div>
      </li>
    </ol>
    <TableEmpty v-else title="暂无学习排行数据" />
  </section>
</template>

<style scoped>
.home-leaderboard {
  min-width: 0;
}

.leaderboard {
  margin: 0;
  padding: 0 20px 16px;
  list-style: none;
}

.leaderboard-row {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) minmax(40px, auto) minmax(40px, auto);
  align-items: center;
  gap: 10px;
  padding: 9px 8px;
  border-top: 1px solid #e4eeea;
  border-radius: 8px;
  transition: background 0.16s ease;
}

.leaderboard-row:hover {
  background: #f5f9f8;
}

.leaderboard-row.is-self {
  border-color: #dceee6;
  border-radius: 8px;
  background: #eef7f3;
}

.leaderboard-rank {
  display: grid;
  place-items: center;
  width: 25px;
  height: 25px;
  border-radius: 7px;
  background: #eef6f4;
  color: var(--text-muted);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.leaderboard-rank.is-first {
  background: #f9edcf;
  color: #77530d;
}

.leaderboard-rank.is-second {
  background: #e8eef2;
  color: #4a5d6d;
}

.leaderboard-rank.is-third {
  background: #f3e6d8;
  color: #7a5330;
}

.leaderboard-person,
.leaderboard-value {
  display: grid;
  gap: 3px;
  color: #234055;
  font-size: 13px;
  overflow-wrap: anywhere;
}

.leaderboard-person small,
.leaderboard-value span {
  color: var(--text-muted);
  font-size: 11px;
}

.leaderboard-value {
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.leaderboard-self {
  display: inline-block;
  padding: 1px 5px;
  border-radius: 4px;
  background: #d9ede3;
  color: var(--workspace-accent);
  font-size: 11px;
}

@media (max-width: 550px) {
  .leaderboard {
    padding-inline: 12px;
  }
  .leaderboard-row {
    gap: 8px;
  }
}
</style>
