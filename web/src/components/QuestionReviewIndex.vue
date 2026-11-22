<script setup lang="ts">
defineProps<{ targetPrefix: string; questions: { answered: boolean }[] }>()

function jump(id: string) {
  const target = document.getElementById(id)
  target?.scrollIntoView({ block: 'start' })
  target?.focus({ preventScroll: true })
}
</script>

<template>
  <nav v-if="questions.length" aria-label="题号索引" class="review-index-buttons">
    <span>题号索引</span>
    <el-button
      v-for="(question, index) in questions"
      :key="index"
      size="small"
      :type="question.answered ? 'primary' : 'info'"
      plain
      :aria-label="`跳转至第 ${index + 1} 题${question.answered ? '' : '，未作答'}`"
      :aria-controls="`${targetPrefix}-${index}`"
      @click="jump(`${targetPrefix}-${index}`)"
      >{{ index + 1 }}</el-button
    >
  </nav>
</template>

<style scoped>
.review-index-buttons {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin: 12px 0 16px;
}

.review-index-buttons span {
  margin-right: 6px;
}

.review-index-buttons :deep(.el-button) {
  margin: 0;
  min-width: 34px;
}
</style>
