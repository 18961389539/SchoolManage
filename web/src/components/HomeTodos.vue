<script setup lang="ts">
import type { HomeTodo } from '../analytics'

defineProps<{ todos: HomeTodo[]; emptyHint?: string }>()
const emit = defineEmits<{ navigate: [route: string, params?: Record<string, string>] }>()

function open(todo: HomeTodo) {
  if (!todo.action) return
  emit('navigate', todo.action.route, todo.action.params)
}
</script>

<template>
  <section class="surface home-todos" aria-labelledby="home-todos-title">
    <header class="card-heading">
      <div>
        <h2 id="home-todos-title">待办</h2>
        <p v-if="todos.length">共 {{ todos.length }} 项需要关注</p>
      </div>
    </header>
    <ul v-if="todos.length" class="home-todo-list">
      <li v-for="todo in todos" :key="todo.key" class="home-todo-item">
        <div>
          <h3>{{ todo.title }}</h3>
          <p>{{ todo.hint }}</p>
        </div>
        <el-button v-if="todo.action" type="primary" plain @click="open(todo)">{{ todo.action.label }}</el-button>
      </li>
    </ul>
    <p v-else class="home-todo-empty">{{ emptyHint || '暂时没有待办。有需要处理的事项时，会出现在这里。' }}</p>
  </section>
</template>

<style scoped>
.home-todos {
  margin-bottom: 18px;
}

.home-todo-list {
  list-style: none;
  margin: 0;
  padding: 0 16px 16px;
  display: grid;
  gap: 10px;
}

.home-todo-item {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 12px 14px;
  border: 1px solid var(--el-border-color-lighter, #e6edf5);
  border-radius: 10px;
  background: #f8fbfc;
}

.home-todo-item h3 {
  margin: 0 0 4px;
  color: #1f3a55;
  font-size: 14px;
  font-weight: 600;
  line-height: 1.4;
}

.home-todo-item p {
  margin: 0;
  color: #56697c;
  font-size: 12px;
  line-height: 1.5;
}

.home-todo-item .el-button {
  flex-shrink: 0;
}

.home-todo-empty {
  margin: 0;
  padding: 4px 16px 18px;
  color: #56697c;
  font-size: 13px;
  line-height: 1.55;
}

@media (max-width: 640px) {
  .home-todo-item {
    flex-wrap: wrap;
  }
}
</style>
