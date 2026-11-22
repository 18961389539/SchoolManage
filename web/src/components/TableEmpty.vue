<script setup lang="ts">
import { Box } from '@element-plus/icons-vue'

defineProps<{ title: string; hint?: string }>()
</script>
<template>
  <div class="table-empty">
    <el-icon :size="30" aria-hidden="true"><Box /></el-icon>
    <strong>{{ title }}</strong>
    <span v-if="hint">{{ hint }}</span>
  </div>
</template>
<style scoped>
.table-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
  padding: 26px 0 30px;
  line-height: 1.5;
}

.table-empty .el-icon {
  color: #b6c5d4;
  margin-bottom: 2px;
}

.table-empty strong {
  color: #465d74;
  font-size: 14px;
  font-weight: 600;
}

.table-empty span {
  color: #64788a;
  font-size: 12px;
}
</style>
