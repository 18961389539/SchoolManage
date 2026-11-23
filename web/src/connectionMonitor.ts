import { createApp } from 'vue'
import ConnectionNotice from './components/ConnectionNotice.vue'

/**
 * A small standalone Vue app for the connection banner. Mounting it outside the main shell
 * keeps the notice visible regardless of which route or dialog is on screen, without adding
 * state to the application root.
 */
const mountPoint = document.createElement('div')
mountPoint.id = 'connection-notice-root'
document.body.append(mountPoint)
createApp(ConnectionNotice).mount(mountPoint)
