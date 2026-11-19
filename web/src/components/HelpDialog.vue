<script setup lang="ts">
const visible = defineModel<boolean>({ required: true })
</script>

<template>
  <el-dialog v-model="visible" title="使用帮助" width="min(720px, 95vw)">
    <div class="help-content">
      <h3>开始配置</h3>
      <p>
        先维护专业与班级，再新增或导入师生档案。导入预览会显示错误的原始行、列和原因，“移除问题行”仅作用于本次待导入数据。
      </p>
      <h3>使用模拟环境</h3>
      <p>
        在控制台点击“创建模拟环境”，随后打开“实训设备”的终端。模拟设备支持 help、show version、show interfaces
        等命令；模拟虚拟机可在虚拟机管理中启动或停止。所有模拟资源均有明确标识。
      </p>
      <h3>结束设备会话</h3>
      <p>关闭终端会结束当前连接；“回收机柜”会关闭该机柜的平台会话并释放占用，不会重置硬件配置。</p>
      <h3>备份与恢复</h3>
      <p>在“备份与恢复”创建并下载备份。备份含业务数据与必要密钥，请妥善保存；恢复请按随包工具和维护文档操作。</p>
      <h3>账号与凭据</h3>
      <p>师生初始密码首次登录时需要修改。设备密码仅用于后端连接，编辑时留空保留原凭据，列表不显示密码。</p>
    </div>
    <template #footer><el-button type="primary" @click="visible = false">我知道了</el-button></template>
  </el-dialog>
</template>
