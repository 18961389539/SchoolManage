import { defineAsyncComponent, type Component } from 'vue'
import PageError from './components/PageError.vue'
import PageLoading from './components/PageLoading.vue'

/**
 * Page-level code splitting with feedback. Without a loading component a slow
 * chunk leaves the content area blank; without an error component a chunk that
 * disappeared after an update leaves the operator with no way forward.
 */
export const lazyPage = (loader: () => Promise<{ default: Component }>) =>
  defineAsyncComponent({
    loader,
    loadingComponent: PageLoading,
    errorComponent: PageError,
    delay: 120,
    timeout: 30000
  })
