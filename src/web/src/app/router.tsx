import {
  createRootRoute,
  createRoute,
  createRouter,
  redirect,
  type RouterHistory,
} from '@tanstack/react-router';
import { ActivityDetailPage, ActivityListPage, UploadPage } from '@/features/activities';
import { AppShell } from './layout/AppShell';
import { NotFoundPage } from './NotFoundPage';

// Instradamento a codice, servito dalla radice (nessun basepath: serve anche al guscio nativo, DEC-015).

const rootRoute = createRootRoute({
  component: AppShell,
  notFoundComponent: NotFoundPage,
});

const indexRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  beforeLoad: () => {
    throw redirect({ to: '/activities', replace: true });
  },
});

const activitiesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'activities',
  component: ActivityListPage,
});

const activityDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'activities/$activityId',
  component: function ActivityDetailRoute() {
    const { activityId } = activityDetailRoute.useParams();
    return <ActivityDetailPage activityId={activityId} />;
  },
});

const uploadRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'upload',
  component: UploadPage,
});

const routeTree = rootRoute.addChildren([indexRoute, activitiesRoute, activityDetailRoute, uploadRoute]);

export function createAppRouter(history?: RouterHistory) {
  return createRouter({ routeTree, history });
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
