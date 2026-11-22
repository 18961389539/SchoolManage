<script setup lang="ts">
import { Box } from '@element-plus/icons-vue'

defineProps<{ title: string; hint?: string }>()
</script>
<template>
  <div class="table-empty">
    <el-icon :size="30" aria-hidden="true"><Box /></el-icon>
    <strong>{{ title }}</strong>
    <span v-if="hint">{{ hint }}</span>
    <div v-if="$slots.action" class="table-empty-action">
      <slot name="action" />
    </div>
  </div>
</template>
<style scoped>
.table-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 36px 16px 40px;
  line-height: 1.5;
}

.table-empty .el-icon {
  display: grid;
  place-items: center;
  width: 56px;
  height: 56px;
  margin-bottom: 4px;
  border-radius: 16px;
  background: #eef5f4;
  color: #5e8f88;
}

.table-empty strong {
  color: #3b5368;
  font-size: 14px;
  font-weight: 600;
}

.table-empty span {
  max-width: 28ch;
  color: #64788a;
  font-size: 12px;
  text-align: center;
}

.table-empty-action {
  margin-top: 6px;
}
</style>
